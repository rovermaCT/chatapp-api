using System.ComponentModel.DataAnnotations;
using ChatApp.Api.Entities;

namespace ChatApp.Api.Contracts;

public enum FriendRequestDirection
{
    Incoming,
    Outgoing
}

public sealed record SendFriendRequestRequest([Required] string UserName);

public sealed record UserSummary(Guid Id, string UserName, string DisplayName);

public sealed record FriendRequestResponse(
    Guid Id,
    UserSummary Sender,
    UserSummary Receiver,
    FriendRequestStatus Status,
    DateTime CreatedAt,
    DateTime? RespondedAt);

public sealed record FriendResponse(UserSummary User, DateTime FriendsSince);
