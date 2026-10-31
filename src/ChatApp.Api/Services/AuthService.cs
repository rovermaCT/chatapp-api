using System.Security.Claims;
using System.Security.Cryptography;
using System.Text;
using System.Text.RegularExpressions;
using ChatApp.Api.Contracts;
using ChatApp.Api.Data;
using ChatApp.Api.Entities;
using ChatApp.Api.Options;
using Microsoft.AspNetCore.Identity;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Caching.Memory;
using Microsoft.Extensions.Options;
using Microsoft.IdentityModel.JsonWebTokens;

namespace ChatApp.Api.Services;

public enum CredentialCheck
{
    Succeeded,
    Invalid,
    LockedOut
}

public sealed partial class AuthService(
    ChatDbContext db,
    UserManager<ApplicationUser> userManager,
    SignInManager<ApplicationUser> signInManager,
    TokenService tokenService,
    IOptions<JwtOptions> jwtOptions,
    IOptions<ExternalAuthOptions> externalOptions,
    IMemoryCache cache,
    TimeProvider time,
    ILogger<AuthService> logger)
{
    private DateTime UtcNow => time.GetUtcNow().UtcDateTime;

    public async Task<TokenResponse> IssueTokensAsync(ApplicationUser user, CancellationToken ct = default)
    {
        var now = UtcNow;
        var accessTokenExpiresAt = now.AddMinutes(jwtOptions.Value.AccessTokenMinutes);
        var roles = await userManager.GetRolesAsync(user);
        var accessToken = tokenService.CreateAccessToken(user, roles, now, accessTokenExpiresAt);

        var refreshToken = TokenService.CreateOpaqueToken();
        db.RefreshTokens.Add(new RefreshToken
        {
            UserId = user.Id,
            TokenHash = TokenService.Hash(refreshToken),
            CreatedAt = now,
            ExpiresAt = now.AddDays(jwtOptions.Value.RefreshTokenDays)
        });
        await db.SaveChangesAsync(ct);

        logger.LogInformation("Issued tokens for user {UserId}; access token expires at {AccessTokenExpiresAt}",
            user.Id, accessTokenExpiresAt);
        return new TokenResponse(
            accessToken,
            "Bearer",
            (int)(accessTokenExpiresAt - now).TotalSeconds,
            refreshToken);
    }

    public async Task<(CredentialCheck Result, ApplicationUser? User)> CheckCredentialsAsync(string userNameOrEmail, string password)
    {
        // User names cannot contain '@' (see RegisterRequest), so this is unambiguous.
        var user = userNameOrEmail.Contains('@')
            ? await userManager.FindByEmailAsync(userNameOrEmail)
            : await userManager.FindByNameAsync(userNameOrEmail);
        if (user is null)
        {
            logger.LogInformation("Sign-in failed: no user matches {LoginName}", userNameOrEmail);
            return (CredentialCheck.Invalid, null);
        }

        var result = await signInManager.CheckPasswordSignInAsync(user, password, lockoutOnFailure: true);
        if (result.IsLockedOut)
        {
            logger.LogWarning("Sign-in rejected: user {UserId} is locked out", user.Id);
            return (CredentialCheck.LockedOut, null);
        }
        if (!result.Succeeded)
        {
            logger.LogWarning("Sign-in failed: invalid password for user {UserId}", user.Id);
            return (CredentialCheck.Invalid, null);
        }

        logger.LogInformation("User {UserId} signed in with password", user.Id);
        return (CredentialCheck.Succeeded, user);
    }

    /// <summary>Rotates a refresh token: the presented token is revoked and a new pair is issued.</summary>
    public async Task<TokenResponse?> RefreshAsync(string refreshToken, CancellationToken ct = default)
    {
        var hash = TokenService.Hash(refreshToken);
        var stored = await db.RefreshTokens
            .Include(t => t.User)
            .AsNoTracking()
            .SingleOrDefaultAsync(t => t.TokenHash == hash, ct);
        if (stored is null)
        {
            logger.LogInformation("Refresh rejected: unknown refresh token");
            return null;
        }

        if (stored.RevokedAt is not null)
        {
            // A rotated token was replayed, so it has probably been stolen: end every session for this user.
            logger.LogWarning("Revoked refresh token reused for user {UserId}; revoking all sessions", stored.UserId);
            await RevokeAllAsync(stored.UserId, ct);
            return null;
        }

        var now = UtcNow;
        if (stored.ExpiresAt <= now)
        {
            logger.LogInformation("Refresh rejected: token {TokenId} for user {UserId} expired at {ExpiresAt}",
                stored.Id, stored.UserId, stored.ExpiresAt);
            return null;
        }
        if (await userManager.IsLockedOutAsync(stored.User))
        {
            logger.LogWarning("Refresh rejected: user {UserId} is locked out", stored.UserId);
            return null;
        }

        // Conditional update so two concurrent refreshes with the same token cannot both succeed.
        var revoked = await db.RefreshTokens
            .Where(t => t.Id == stored.Id && t.RevokedAt == null)
            .ExecuteUpdateAsync(s => s.SetProperty(t => t.RevokedAt, now), ct);
        if (revoked == 0)
        {
            logger.LogWarning("Refresh rejected: token {TokenId} for user {UserId} was rotated concurrently",
                stored.Id, stored.UserId);
            return null;
        }

        logger.LogInformation("Rotated refresh token {TokenId} for user {UserId}", stored.Id, stored.UserId);
        return await IssueTokensAsync(stored.User, ct);
    }

    public async Task<int> RevokeAsync(Guid userId, string refreshToken, CancellationToken ct = default)
    {
        var hash = TokenService.Hash(refreshToken);
        var count = await db.RefreshTokens
            .Where(t => t.UserId == userId && t.TokenHash == hash && t.RevokedAt == null)
            .ExecuteUpdateAsync(s => s.SetProperty(t => t.RevokedAt, UtcNow), ct);
        logger.LogInformation("Revoked {Count} refresh token(s) for user {UserId}", count, userId);
        return count;
    }

    public async Task<int> RevokeAllAsync(Guid userId, CancellationToken ct = default)
    {
        var count = await db.RefreshTokens
            .Where(t => t.UserId == userId && t.RevokedAt == null)
            .ExecuteUpdateAsync(s => s.SetProperty(t => t.RevokedAt, UtcNow), ct);
        logger.LogInformation("Revoked all {Count} active refresh token(s) for user {UserId}", count, userId);
        return count;
    }

    /// <summary>
    /// Maps an authenticated OIDC principal to a local user, creating or linking the account on first login.
    /// </summary>
    public async Task<(ApplicationUser? User, string? Error)> FindOrCreateExternalUserAsync(string provider, ClaimsPrincipal principal)
    {
        var subject = principal.FindFirstValue(JwtRegisteredClaimNames.Sub);
        if (string.IsNullOrEmpty(subject))
        {
            logger.LogWarning("External login from {Provider} rejected: no subject identifier", provider);
            return (null, "The identity provider did not return a subject identifier.");
        }

        var user = await userManager.FindByLoginAsync(provider, subject);
        if (user is not null)
        {
            logger.LogInformation("External login from {Provider} matched existing user {UserId}", provider, user.Id);
            return (user, null);
        }

        var email = principal.FindFirstValue(JwtRegisteredClaimNames.Email);
        var emailVerified = bool.TryParse(principal.FindFirstValue("email_verified"), out var verified) && verified;
        var name = principal.FindFirstValue("name");

        if (email is not null)
        {
            var existing = await userManager.FindByEmailAsync(email);
            if (existing is not null)
            {
                // Only auto-link when both sides have verified the email. Otherwise someone could register
                // "victim@example.com" at a provider (or locally, before the victim) and hijack the account.
                if (!emailVerified || !existing.EmailConfirmed)
                {
                    logger.LogWarning(
                        "External login from {Provider} not linked to user {UserId}: email not verified (provider {ProviderEmailVerified}, local {LocalEmailConfirmed})",
                        provider, existing.Id, emailVerified, existing.EmailConfirmed);
                    return (null, "An account with this email already exists. Sign in with your password to continue.");
                }
                user = existing;
            }
        }

        if (user is null)
        {
            var userName = await GenerateUniqueUserNameAsync(email?.Split('@')[0] ?? name ?? provider);
            user = new ApplicationUser
            {
                UserName = userName,
                Email = email,
                EmailConfirmed = emailVerified,
                DisplayName = Truncate(name ?? userName, 64),
                CreatedAt = UtcNow
            };
            var created = await userManager.CreateAsync(user);
            if (!created.Succeeded)
            {
                logger.LogWarning("Could not create user for {Provider} login: {@Errors}",
                    provider, created.Errors.Select(e => e.Code));
                return (null, string.Join(" ", created.Errors.Select(e => e.Description)));
            }
            logger.LogInformation("Created user {UserId} ({UserName}) from {Provider} login", user.Id, userName, provider);
        }

        var linked = await userManager.AddLoginAsync(user, new UserLoginInfo(provider, subject, provider));
        if (!linked.Succeeded)
        {
            logger.LogWarning("Could not link {Provider} login to user {UserId}: {@Errors}",
                provider, user.Id, linked.Errors.Select(e => e.Code));
            return (null, string.Join(" ", linked.Errors.Select(e => e.Description)));
        }

        logger.LogInformation("Linked {Provider} login to user {UserId}", provider, user.Id);
        return (user, null);
    }

    /// <summary>Issues a short-lived, single-use code the client exchanges at <c>/connect/token</c>.</summary>
    public string CreateAuthorizationCode(Guid userId, string redirectUri, string? codeChallenge)
    {
        var code = TokenService.CreateOpaqueToken(32);
        cache.Set(
            AuthorizationCodeKey(code),
            new PendingAuthorization(userId, redirectUri, codeChallenge),
            TimeSpan.FromSeconds(externalOptions.Value.AuthorizationCodeLifetimeSeconds));
        logger.LogDebug("Issued authorization code for user {UserId} (PKCE: {UsesPkce})", userId, codeChallenge is not null);
        return code;
    }

    public async Task<ApplicationUser?> RedeemAuthorizationCodeAsync(string code, string? redirectUri, string? codeVerifier)
    {
        var key = AuthorizationCodeKey(code);
        if (!cache.TryGetValue(key, out PendingAuthorization? pending) || pending is null)
        {
            logger.LogWarning("Authorization code rejected: {Reason}", "unknown, expired or already used");
            return null;
        }
        cache.Remove(key);

        if (!string.Equals(pending.RedirectUri, redirectUri, StringComparison.Ordinal))
        {
            logger.LogWarning("Authorization code for user {UserId} rejected: {Reason}", pending.UserId, "redirect_uri mismatch");
            return null;
        }
        if (pending.CodeChallenge is not null && !VerifyPkce(pending.CodeChallenge, codeVerifier))
        {
            logger.LogWarning("Authorization code for user {UserId} rejected: {Reason}", pending.UserId, "PKCE verification failed");
            return null;
        }

        var user = await userManager.FindByIdAsync(pending.UserId.ToString());
        if (user is null)
        {
            logger.LogWarning("Authorization code rejected: user {UserId} no longer exists", pending.UserId);
        }
        return user;
    }

    private static bool VerifyPkce(string codeChallenge, string? codeVerifier)
    {
        if (string.IsNullOrEmpty(codeVerifier))
        {
            return false;
        }
        var computed = System.Buffers.Text.Base64Url.EncodeToString(SHA256.HashData(Encoding.ASCII.GetBytes(codeVerifier)));
        return CryptographicOperations.FixedTimeEquals(Encoding.ASCII.GetBytes(computed), Encoding.ASCII.GetBytes(codeChallenge));
    }

    private async Task<string> GenerateUniqueUserNameAsync(string seed)
    {
        var baseName = Truncate(InvalidUserNameChars().Replace(seed, ""), 24);
        if (baseName.Length < 3)
        {
            baseName = "user";
        }

        var candidate = baseName;
        while (await userManager.FindByNameAsync(candidate) is not null)
        {
            candidate = $"{baseName}{RandomNumberGenerator.GetInt32(1000, 100000)}";
        }
        return candidate;
    }

    private static string Truncate(string value, int maxLength) =>
        value.Length <= maxLength ? value : value[..maxLength];

    private static string AuthorizationCodeKey(string code) => $"authcode:{TokenService.Hash(code)}";

    [GeneratedRegex("[^a-zA-Z0-9_.-]")]
    private static partial Regex InvalidUserNameChars();

    private sealed record PendingAuthorization(Guid UserId, string RedirectUri, string? CodeChallenge);
}
