namespace ChatApp.Api.Entities;

/// <summary>
/// A long-lived, single-use token that can be exchanged for a new access token.
/// Only a SHA-256 hash of the token is stored, so a database leak does not leak usable tokens.
/// </summary>
public class RefreshToken
{
    public Guid Id { get; set; }

    public Guid UserId { get; set; }

    public ApplicationUser User { get; set; } = null!;

    public string TokenHash { get; set; } = string.Empty;

    public DateTime CreatedAt { get; set; }

    public DateTime ExpiresAt { get; set; }

    public DateTime? RevokedAt { get; set; }
}
