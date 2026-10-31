using System.Buffers.Text;
using System.Security.Claims;
using System.Security.Cryptography;
using System.Text;
using ChatApp.Api.Entities;
using ChatApp.Api.Options;
using Microsoft.Extensions.Options;
using Microsoft.IdentityModel.JsonWebTokens;
using Microsoft.IdentityModel.Tokens;

namespace ChatApp.Api.Services;

public sealed class TokenService(IOptions<JwtOptions> options, ILogger<TokenService> logger)
{
    private readonly JsonWebTokenHandler _handler = new();

    public string CreateAccessToken(ApplicationUser user, IEnumerable<string> roles, DateTime now, DateTime expiresAt)
    {
        var jwt = options.Value;

        var jti = Guid.NewGuid().ToString();
        var claims = new List<Claim>
        {
            new(JwtRegisteredClaimNames.Sub, user.Id.ToString()),
            new(JwtRegisteredClaimNames.Jti, jti),
            new(JwtRegisteredClaimNames.UniqueName, user.UserName!),
            new(AppClaimTypes.DisplayName, user.DisplayName)
        };
        if (user.Email is not null)
        {
            claims.Add(new Claim(JwtRegisteredClaimNames.Email, user.Email));
        }
        claims.AddRange(roles.Select(role => new Claim(AppClaimTypes.Role, role)));

        logger.LogDebug("Creating access token {Jti} for user {UserId}, expires at {ExpiresAt}", jti, user.Id, expiresAt);
        return _handler.CreateToken(new SecurityTokenDescriptor
        {
            Issuer = jwt.Issuer,
            Audience = jwt.Audience,
            Subject = new ClaimsIdentity(claims),
            IssuedAt = now,
            NotBefore = now,
            Expires = expiresAt,
            SigningCredentials = new SigningCredentials(jwt.GetSigningKey(), SecurityAlgorithms.HmacSha256)
        });
    }

    /// <summary>Cryptographically random, URL-safe opaque token.</summary>
    public static string CreateOpaqueToken(int byteLength = 64) =>
        Base64Url.EncodeToString(RandomNumberGenerator.GetBytes(byteLength));

    public static string Hash(string token) =>
        Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(token)));
}

public static class AppClaimTypes
{
    public const string DisplayName = "display_name";
    public const string Role = "role";
}
