using System.ComponentModel.DataAnnotations;
using System.Text.Json.Serialization;
using Microsoft.AspNetCore.Mvc;

namespace ChatApp.Api.Contracts;

public sealed record RegisterRequest(
    [Required, EmailAddress, StringLength(256)] string Email,
    [Required, StringLength(32, MinimumLength = 3), RegularExpression("^[a-zA-Z0-9_.-]+$",
        ErrorMessage = "User name may only contain letters, digits, '_', '.' and '-'.")] string UserName,
    [Required] string Password,
    [StringLength(64)] string? DisplayName);

public sealed record LoginRequest(
    [Required] string UserNameOrEmail,
    [Required] string Password);

public sealed record LogoutRequest(string? RefreshToken);

public sealed record UserResponse(
    Guid Id,
    string UserName,
    string? Email,
    string DisplayName,
    IReadOnlyList<string> ExternalLogins);

public sealed record ExternalProviderResponse(string Name, string DisplayName, string LoginUrl);

/// <summary>OAuth 2.0 token response (RFC 6749 §5.1).</summary>
public sealed record TokenResponse(
    [property: JsonPropertyName("access_token")] string AccessToken,
    [property: JsonPropertyName("token_type")] string TokenType,
    [property: JsonPropertyName("expires_in")] int ExpiresIn,
    [property: JsonPropertyName("refresh_token")] string RefreshToken);

/// <summary>OAuth 2.0 error response (RFC 6749 §5.2).</summary>
public sealed record OAuthErrorResponse(
    [property: JsonPropertyName("error")] string Error,
    [property: JsonPropertyName("error_description")] string? ErrorDescription);

/// <summary>Form body of <c>POST /connect/token</c>. Every field is optional because which ones apply depends on the grant.</summary>
public sealed class OAuthTokenRequest
{
    [FromForm(Name = "grant_type")]
    public string? GrantType { get; set; }

    [FromForm(Name = "username")]
    public string? UserName { get; set; }

    [FromForm(Name = "password")]
    public string? Password { get; set; }

    [FromForm(Name = "refresh_token")]
    public string? RefreshToken { get; set; }

    [FromForm(Name = "code")]
    public string? Code { get; set; }

    [FromForm(Name = "redirect_uri")]
    public string? RedirectUri { get; set; }

    [FromForm(Name = "code_verifier")]
    public string? CodeVerifier { get; set; }
}
