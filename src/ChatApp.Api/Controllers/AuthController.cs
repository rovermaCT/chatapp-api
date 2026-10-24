using ChatApp.Api.Contracts;
using ChatApp.Api.Extensions;
using ChatApp.Api.Entities;
using ChatApp.Api.Options;
using ChatApp.Api.Services;
using Microsoft.AspNetCore.Authentication;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Identity;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.RateLimiting;
using Microsoft.AspNetCore.WebUtilities;
using Microsoft.Extensions.Options;

namespace ChatApp.Api.Controllers;

[Route("api/auth")]
[EnableRateLimiting(RateLimitPolicies.Auth)]
public sealed class AuthController(
    AuthService auth,
    UserManager<ApplicationUser> userManager,
    IOptions<ExternalAuthOptions> externalOptions,
    IWebHostEnvironment environment,
    TimeProvider time) : ApiControllerBase
{
    [HttpPost("register")]
    [AllowAnonymous]
    [ProducesResponseType<TokenResponse>(StatusCodes.Status201Created)]
    [ProducesResponseType<ValidationProblemDetails>(StatusCodes.Status400BadRequest)]
    public async Task<ActionResult<TokenResponse>> Register(RegisterRequest request, CancellationToken ct)
    {
        var user = new ApplicationUser
        {
            UserName = request.UserName,
            Email = request.Email,
            DisplayName = string.IsNullOrWhiteSpace(request.DisplayName) ? request.UserName : request.DisplayName.Trim(),
            CreatedAt = time.GetUtcNow().UtcDateTime
        };

        var result = await userManager.CreateAsync(user, request.Password);
        if (!result.Succeeded)
        {
            foreach (var error in result.Errors)
            {
                ModelState.AddModelError(error.Code, error.Description);
            }
            return ValidationProblem(ModelState);
        }

        var tokens = await auth.IssueTokensAsync(user, ct);
        return CreatedAtAction(nameof(Me), null, tokens);
    }

    [HttpPost("login")]
    [AllowAnonymous]
    [ProducesResponseType<TokenResponse>(StatusCodes.Status200OK)]
    [ProducesResponseType<ProblemDetails>(StatusCodes.Status401Unauthorized)]
    public async Task<ActionResult<TokenResponse>> Login(LoginRequest request, CancellationToken ct)
    {
        var (result, user) = await auth.CheckCredentialsAsync(request.UserNameOrEmail, request.Password);
        return result switch
        {
            CredentialCheck.Succeeded => Ok(await auth.IssueTokensAsync(user!, ct)),
            CredentialCheck.LockedOut => Problem(statusCode: StatusCodes.Status401Unauthorized, title: "Account locked",
                detail: "Too many failed attempts. Try again later."),
            _ => Problem(statusCode: StatusCodes.Status401Unauthorized, title: "Invalid credentials",
                detail: "The user name/email or password is incorrect.")
        };
    }

    [HttpPost("logout")]
    [Authorize]
    [ProducesResponseType(StatusCodes.Status204NoContent)]
    public async Task<IActionResult> Logout(LogoutRequest request, CancellationToken ct)
    {
        // With a refresh token only that session ends; without one, every session for the user does.
        if (string.IsNullOrEmpty(request.RefreshToken))
        {
            await auth.RevokeAllAsync(CurrentUserId, ct);
        }
        else
        {
            await auth.RevokeAsync(CurrentUserId, request.RefreshToken, ct);
        }
        return NoContent();
    }

    [HttpGet("me")]
    [Authorize]
    [DisableRateLimiting]
    [ProducesResponseType<UserResponse>(StatusCodes.Status200OK)]
    public async Task<ActionResult<UserResponse>> Me()
    {
        var user = await userManager.FindByIdAsync(CurrentUserId.ToString());
        if (user is null)
        {
            return Unauthorized();
        }

        var logins = await userManager.GetLoginsAsync(user);
        return new UserResponse(user.Id, user.UserName!, user.Email, user.DisplayName,
            logins.Select(l => l.LoginProvider).ToList());
    }

    [HttpGet("providers")]
    [AllowAnonymous]
    [DisableRateLimiting]
    public ActionResult<IEnumerable<ExternalProviderResponse>> Providers() =>
        Ok(externalOptions.Value.ConfiguredProviders().Select(p => new ExternalProviderResponse(
            p.Key,
            p.Value.DisplayName ?? p.Key,
            Url.Action(nameof(ExternalLogin), new { provider = p.Key })!)));

    /// <summary>
    /// Starts an OpenID Connect login with an external provider (authorization code + PKCE against the provider).
    /// After the provider redirects back, the client's <paramref name="redirectUri"/> receives a one-time
    /// <c>code</c> to exchange at <c>POST /connect/token</c> with <c>grant_type=authorization_code</c>.
    /// </summary>
    [HttpGet("external/{provider}")]
    [AllowAnonymous]
    [ProducesResponseType(StatusCodes.Status302Found)]
    public IActionResult ExternalLogin(
        string provider,
        [FromQuery(Name = "redirect_uri")] string? redirectUri,
        [FromQuery(Name = "state")] string? state,
        [FromQuery(Name = "code_challenge")] string? codeChallenge,
        [FromQuery(Name = "code_challenge_method")] string? codeChallengeMethod)
    {
        var scheme = externalOptions.Value.ConfiguredProviders()
            .Select(p => p.Key)
            .FirstOrDefault(name => string.Equals(name, provider, StringComparison.OrdinalIgnoreCase));
        if (scheme is null)
        {
            return Problem(statusCode: StatusCodes.Status404NotFound, title: "Unknown provider",
                detail: $"External provider '{provider}' is not configured.");
        }

        if (redirectUri is null)
        {
            // Returning tokens straight from the callback is handy for trying the flow in a browser,
            // but real clients must use a registered redirect URI and the code exchange.
            if (!environment.IsDevelopment())
            {
                return Problem(statusCode: StatusCodes.Status400BadRequest, title: "invalid_request",
                    detail: "redirect_uri is required.");
            }
        }
        else if (!externalOptions.Value.AllowedRedirectUris.Contains(redirectUri, StringComparer.Ordinal))
        {
            return Problem(statusCode: StatusCodes.Status400BadRequest, title: "invalid_request",
                detail: "redirect_uri is not registered.");
        }

        if (codeChallenge is not null && codeChallengeMethod != "S256")
        {
            return Problem(statusCode: StatusCodes.Status400BadRequest, title: "invalid_request",
                detail: "Only code_challenge_method=S256 is supported.");
        }

        var properties = new AuthenticationProperties { RedirectUri = Url.Action(nameof(ExternalCallback)) };
        properties.Items[ExternalAuthItems.Provider] = scheme;
        properties.Items[ExternalAuthItems.RedirectUri] = redirectUri;
        properties.Items[ExternalAuthItems.State] = state;
        properties.Items[ExternalAuthItems.CodeChallenge] = codeChallenge;
        return Challenge(properties, scheme);
    }

    [HttpGet("external/callback")]
    [AllowAnonymous]
    [ApiExplorerSettings(IgnoreApi = true)]
    public async Task<IActionResult> ExternalCallback(CancellationToken ct)
    {
        var result = await HttpContext.AuthenticateAsync(IdentityConstants.ExternalScheme);
        await HttpContext.SignOutAsync(IdentityConstants.ExternalScheme);

        if (!result.Succeeded
            || result.Properties.GetString(ExternalAuthItems.Provider) is not { } provider)
        {
            return Problem(statusCode: StatusCodes.Status401Unauthorized, title: "External login failed",
                detail: "No external login session was found. Start again from /api/auth/external/{provider}.");
        }

        var redirectUri = result.Properties.GetString(ExternalAuthItems.RedirectUri);
        var state = result.Properties.GetString(ExternalAuthItems.State);

        var (user, error) = await auth.FindOrCreateExternalUserAsync(provider, result.Principal);
        if (user is null)
        {
            return redirectUri is null
                ? Problem(statusCode: StatusCodes.Status409Conflict, title: "External login failed", detail: error)
                : Redirect(QueryHelpers.AddQueryString(redirectUri, new Dictionary<string, string?>
                {
                    ["error"] = "access_denied",
                    ["error_description"] = error,
                    ["state"] = state
                }));
        }

        if (redirectUri is null)
        {
            return Ok(await auth.IssueTokensAsync(user, ct));
        }

        var code = auth.CreateAuthorizationCode(user.Id, redirectUri, result.Properties.GetString(ExternalAuthItems.CodeChallenge));
        return Redirect(QueryHelpers.AddQueryString(redirectUri, new Dictionary<string, string?>
        {
            ["code"] = code,
            ["state"] = state
        }));
    }
}

/// <summary>Keys stored in the OIDC <see cref="AuthenticationProperties"/> across the provider round-trip.</summary>
public static class ExternalAuthItems
{
    public const string Provider = "provider";
    public const string RedirectUri = "client_redirect_uri";
    public const string State = "client_state";
    public const string CodeChallenge = "client_code_challenge";
}
