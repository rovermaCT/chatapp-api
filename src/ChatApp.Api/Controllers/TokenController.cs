using ChatApp.Api.Contracts;
using ChatApp.Api.Extensions;
using ChatApp.Api.Services;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.RateLimiting;

namespace ChatApp.Api.Controllers;

/// <summary>OAuth 2.0 token endpoint (RFC 6749 §3.2).</summary>
[Route("connect")]
[AllowAnonymous]
[EnableRateLimiting(RateLimitPolicies.Auth)]
public sealed class TokenController(AuthService auth, ILogger<TokenController> logger) : ApiControllerBase
{
    /// <summary>
    /// Supported grants: <c>password</c> (first-party clients), <c>refresh_token</c>, and
    /// <c>authorization_code</c> (the code returned after an external OIDC login).
    /// </summary>
    [HttpPost("token")]
    [Consumes("application/x-www-form-urlencoded")]
    [ProducesResponseType<TokenResponse>(StatusCodes.Status200OK)]
    [ProducesResponseType<OAuthErrorResponse>(StatusCodes.Status400BadRequest)]
    public async Task<IActionResult> Token([FromForm] OAuthTokenRequest request, CancellationToken ct)
    {
        Response.Headers.CacheControl = "no-store";
        Response.Headers.Pragma = "no-cache";

        // Tags every log written while handling this grant, including those from AuthService.
        using var _ = logger.BeginScope(new Dictionary<string, object?> { ["GrantType"] = request.GrantType });

        switch (request.GrantType)
        {
            case "password":
            {
                if (string.IsNullOrEmpty(request.UserName) || string.IsNullOrEmpty(request.Password))
                {
                    return OAuthError("invalid_request", "username and password are required.");
                }
                var (result, user) = await auth.CheckCredentialsAsync(request.UserName, request.Password);
                return result == CredentialCheck.Succeeded
                    ? Ok(await auth.IssueTokensAsync(user!, ct))
                    : OAuthError("invalid_grant", result == CredentialCheck.LockedOut
                        ? "The account is locked. Try again later."
                        : "The user name or password is incorrect.");
            }

            case "refresh_token":
            {
                if (string.IsNullOrEmpty(request.RefreshToken))
                {
                    return OAuthError("invalid_request", "refresh_token is required.");
                }
                var tokens = await auth.RefreshAsync(request.RefreshToken, ct);
                return tokens is null
                    ? OAuthError("invalid_grant", "The refresh token is invalid, expired or revoked.")
                    : Ok(tokens);
            }

            case "authorization_code":
            {
                if (string.IsNullOrEmpty(request.Code))
                {
                    return OAuthError("invalid_request", "code is required.");
                }
                var user = await auth.RedeemAuthorizationCodeAsync(request.Code, request.RedirectUri, request.CodeVerifier);
                return user is null
                    ? OAuthError("invalid_grant", "The authorization code is invalid, expired, or does not match redirect_uri/code_verifier.")
                    : Ok(await auth.IssueTokensAsync(user, ct));
            }

            default:
                logger.LogWarning("Token request with unsupported grant type {GrantType}", request.GrantType);
                return OAuthError("unsupported_grant_type", $"grant_type '{request.GrantType}' is not supported.");
        }
    }

    private BadRequestObjectResult OAuthError(string error, string description)
    {
        logger.LogDebug("Token request failed with {OAuthError}: {OAuthErrorDescription}", error, description);
        return BadRequest(new OAuthErrorResponse(error, description));
    }
}
