using System.Linq.Expressions;
using ChatApp.Api.Contracts;
using ChatApp.Api.Data;
using ChatApp.Api.Entities;
using Microsoft.AspNetCore.Identity;
using Microsoft.EntityFrameworkCore;

namespace ChatApp.Api.Services;

public sealed class FriendService(
    ChatDbContext db,
    UserManager<ApplicationUser> userManager,
    TimeProvider time,
    ILogger<FriendService> logger)
{
    private static readonly Expression<Func<FriendRequest, FriendRequestResponse>> ToResponse = r =>
        new FriendRequestResponse(
            r.Id,
            new UserSummary(r.Sender.Id, r.Sender.UserName!, r.Sender.DisplayName),
            new UserSummary(r.Receiver.Id, r.Receiver.UserName!, r.Receiver.DisplayName),
            r.Status,
            r.CreatedAt,
            r.RespondedAt);

    private DateTime UtcNow => time.GetUtcNow().UtcDateTime;

    public Task<List<FriendResponse>> GetFriendsAsync(Guid userId, CancellationToken ct) =>
        db.Friendships
            .Where(f => f.UserId == userId)
            .OrderBy(f => f.Friend.DisplayName)
            .Select(f => new FriendResponse(
                new UserSummary(f.Friend.Id, f.Friend.UserName!, f.Friend.DisplayName),
                f.CreatedAt))
            .ToListAsync(ct);

    public async Task<ServiceResult<bool>> RemoveFriendAsync(Guid userId, Guid friendId, CancellationToken ct)
    {
        var removed = await db.Friendships
            .Where(f => (f.UserId == userId && f.FriendId == friendId) || (f.UserId == friendId && f.FriendId == userId))
            .ExecuteDeleteAsync(ct);
        if (removed == 0)
        {
            logger.LogDebug("User {UserId} tried to remove {FriendId}, who is not a friend", userId, friendId);
            return ServiceError.NotFound("You are not friends with this user.");
        }

        logger.LogInformation("User {UserId} removed friend {FriendId}", userId, friendId);
        return true;
    }

    public Task<List<FriendRequestResponse>> GetPendingRequestsAsync(Guid userId, FriendRequestDirection direction, CancellationToken ct)
    {
        var pending = db.FriendRequests.Where(r => r.Status == FriendRequestStatus.Pending);
        pending = direction == FriendRequestDirection.Incoming
            ? pending.Where(r => r.ReceiverId == userId)
            : pending.Where(r => r.SenderId == userId);

        return pending
            .OrderByDescending(r => r.CreatedAt)
            .Select(ToResponse)
            .ToListAsync(ct);
    }

    public async Task<ServiceResult<FriendRequestResponse>> GetRequestAsync(Guid userId, Guid requestId, CancellationToken ct)
    {
        var request = await db.FriendRequests
            .Where(r => r.Id == requestId && (r.SenderId == userId || r.ReceiverId == userId))
            .Select(ToResponse)
            .SingleOrDefaultAsync(ct);
        return request is null ? RequestNotFound() : request;
    }

    public async Task<ServiceResult<FriendRequestResponse>> SendRequestAsync(Guid senderId, string receiverUserName, CancellationToken ct)
    {
        var normalizedName = userManager.NormalizeName(receiverUserName);
        var receiverId = await db.Users
            .Where(u => u.NormalizedUserName == normalizedName)
            .Select(u => (Guid?)u.Id)
            .SingleOrDefaultAsync(ct);
        if (receiverId is not { } targetId)
        {
            logger.LogDebug("Friend request from {SenderId} rejected: user {ReceiverUserName} not found", senderId, receiverUserName);
            return ServiceError.NotFound($"User '{receiverUserName}' was not found.");
        }
        if (targetId == senderId)
        {
            logger.LogDebug("Friend request from {SenderId} rejected: sent to self", senderId);
            return ServiceError.BadRequest("You cannot send a friend request to yourself.");
        }
        if (await db.Friendships.AnyAsync(f => f.UserId == senderId && f.FriendId == targetId, ct))
        {
            logger.LogDebug("Friend request from {SenderId} to {ReceiverId} rejected: already friends", senderId, targetId);
            return ServiceError.Conflict("You are already friends with this user.");
        }

        var existing = await db.FriendRequests
            .Where(r => r.Status == FriendRequestStatus.Pending
                && ((r.SenderId == senderId && r.ReceiverId == targetId) || (r.SenderId == targetId && r.ReceiverId == senderId)))
            .Select(r => new { r.Id, r.SenderId })
            .FirstOrDefaultAsync(ct);
        if (existing is not null)
        {
            logger.LogDebug("Friend request from {SenderId} to {ReceiverId} rejected: pending request {RequestId} exists",
                senderId, targetId, existing.Id);
            return ServiceError.Conflict(existing.SenderId == senderId
                ? "You already have a pending friend request to this user."
                : $"This user has already sent you a friend request. Accept request {existing.Id} instead.");
        }

        var request = new FriendRequest
        {
            SenderId = senderId,
            ReceiverId = targetId,
            Status = FriendRequestStatus.Pending,
            CreatedAt = UtcNow
        };
        db.FriendRequests.Add(request);

        try
        {
            await db.SaveChangesAsync(ct);
        }
        catch (DbUpdateException ex)
        {
            // Lost a race with an identical request; the filtered unique index rejected the duplicate.
            logger.LogWarning(ex, "Friend request from {SenderId} to {ReceiverId} lost a race with a duplicate", senderId, targetId);
            return ServiceError.Conflict("You already have a pending friend request to this user.");
        }

        logger.LogInformation("Friend request {RequestId} sent from {SenderId} to {ReceiverId}", request.Id, senderId, targetId);
        return await db.FriendRequests.Where(r => r.Id == request.Id).Select(ToResponse).SingleAsync(ct);
    }

    public async Task<ServiceResult<FriendRequestResponse>> AcceptAsync(Guid userId, Guid requestId, CancellationToken ct)
    {
        var request = await db.FriendRequests.SingleOrDefaultAsync(r => r.Id == requestId && r.ReceiverId == userId, ct);
        if (request is null)
        {
            return RequestNotFound();
        }
        if (request.Status != FriendRequestStatus.Pending)
        {
            return AlreadyResolved(request);
        }

        var now = UtcNow;
        request.Status = FriendRequestStatus.Accepted;
        request.RespondedAt = now;

        // If both users had sent each other requests, the reverse one is settled by this acceptance too.
        await db.FriendRequests
            .Where(r => r.SenderId == request.ReceiverId && r.ReceiverId == request.SenderId && r.Status == FriendRequestStatus.Pending)
            .ForEachAsync(r =>
            {
                r.Status = FriendRequestStatus.Accepted;
                r.RespondedAt = now;
            }, ct);

        if (!await db.Friendships.AnyAsync(f => f.UserId == request.SenderId && f.FriendId == request.ReceiverId, ct))
        {
            db.Friendships.AddRange(
                new Friendship { UserId = request.SenderId, FriendId = request.ReceiverId, CreatedAt = now },
                new Friendship { UserId = request.ReceiverId, FriendId = request.SenderId, CreatedAt = now });
        }

        try
        {
            await db.SaveChangesAsync(ct);
        }
        catch (DbUpdateException ex)
        {
            logger.LogWarning(ex, "Accepting friend request {RequestId} by user {UserId} conflicted with a concurrent change",
                requestId, userId);
            return ServiceError.Conflict("This friend request was already handled.");
        }

        logger.LogInformation("Friend request {RequestId} accepted by user {UserId}; now friends with {FriendId}",
            request.Id, userId, request.SenderId);
        return await LoadAsync(request.Id, ct);
    }

    public Task<ServiceResult<FriendRequestResponse>> DeclineAsync(Guid userId, Guid requestId, CancellationToken ct) =>
        CloseAsync(userId, requestId, r => r.Id == requestId && r.ReceiverId == userId, FriendRequestStatus.Declined, ct);

    public Task<ServiceResult<FriendRequestResponse>> CancelAsync(Guid userId, Guid requestId, CancellationToken ct) =>
        CloseAsync(userId, requestId, r => r.Id == requestId && r.SenderId == userId, FriendRequestStatus.Cancelled, ct);

    private async Task<ServiceResult<FriendRequestResponse>> CloseAsync(
        Guid userId, Guid requestId, Expression<Func<FriendRequest, bool>> match, FriendRequestStatus newStatus, CancellationToken ct)
    {
        var request = await db.FriendRequests.SingleOrDefaultAsync(match, ct);
        if (request is null)
        {
            logger.LogDebug("Friend request {RequestId} not found for user {UserId}", requestId, userId);
            return RequestNotFound();
        }
        if (request.Status != FriendRequestStatus.Pending)
        {
            return AlreadyResolved(request);
        }

        request.Status = newStatus;
        request.RespondedAt = UtcNow;
        await db.SaveChangesAsync(ct);
        logger.LogInformation("Friend request {RequestId} {Status} by user {UserId}", request.Id, newStatus, userId);
        return await LoadAsync(request.Id, ct);
    }

    private Task<FriendRequestResponse> LoadAsync(Guid requestId, CancellationToken ct) =>
        db.FriendRequests.Where(r => r.Id == requestId).Select(ToResponse).SingleAsync(ct);

    // Requests the caller is not party to are reported as missing rather than forbidden, so ids can't be probed.
    private static ServiceError RequestNotFound() => ServiceError.NotFound("Friend request not found.");

    private static ServiceError AlreadyResolved(FriendRequest request) =>
        ServiceError.Conflict($"This friend request has already been {request.Status.ToString().ToLowerInvariant()}.");
}
