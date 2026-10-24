namespace ChatApp.Api.Entities;

public enum FriendRequestStatus
{
    Pending = 0,
    Accepted = 1,
    Declined = 2,
    Cancelled = 3
}

public class FriendRequest
{
    public Guid Id { get; set; }

    public Guid SenderId { get; set; }

    public ApplicationUser Sender { get; set; } = null!;

    public Guid ReceiverId { get; set; }

    public ApplicationUser Receiver { get; set; } = null!;

    public FriendRequestStatus Status { get; set; }

    public DateTime CreatedAt { get; set; }

    public DateTime? RespondedAt { get; set; }
}
