namespace ChatApp.Api.Entities;

/// <summary>
/// One direction of a friendship. Accepting a request stores two rows (A→B and B→A)
/// so "list my friends" is a simple lookup on <see cref="UserId"/>.
/// </summary>
public class Friendship
{
    public Guid UserId { get; set; }

    public ApplicationUser User { get; set; } = null!;

    public Guid FriendId { get; set; }

    public ApplicationUser Friend { get; set; } = null!;

    public DateTime CreatedAt { get; set; }
}
