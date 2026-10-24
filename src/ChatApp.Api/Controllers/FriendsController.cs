using ChatApp.Api.Contracts;
using ChatApp.Api.Services;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;

namespace ChatApp.Api.Controllers;

[Route("api/friends")]
[Authorize]
public sealed class FriendsController(FriendService friends) : ApiControllerBase
{
    [HttpGet]
    public async Task<ActionResult<IEnumerable<FriendResponse>>> GetFriends(CancellationToken ct) =>
        await friends.GetFriendsAsync(CurrentUserId, ct);

    [HttpDelete("{friendId:guid}")]
    [ProducesResponseType(StatusCodes.Status204NoContent)]
    [ProducesResponseType<ProblemDetails>(StatusCodes.Status404NotFound)]
    public async Task<IActionResult> RemoveFriend(Guid friendId, CancellationToken ct)
    {
        var result = await friends.RemoveFriendAsync(CurrentUserId, friendId, ct);
        return result.Error is { } error ? ProblemFor(error) : NoContent();
    }

    /// <summary>Pending friend requests sent to (<c>incoming</c>) or by (<c>outgoing</c>) the current user.</summary>
    [HttpGet("requests")]
    public async Task<ActionResult<IEnumerable<FriendRequestResponse>>> GetRequests(
        [FromQuery] FriendRequestDirection direction = FriendRequestDirection.Incoming,
        CancellationToken ct = default) =>
        await friends.GetPendingRequestsAsync(CurrentUserId, direction, ct);

    [HttpGet("requests/{id:guid}")]
    [ProducesResponseType<FriendRequestResponse>(StatusCodes.Status200OK)]
    [ProducesResponseType<ProblemDetails>(StatusCodes.Status404NotFound)]
    public async Task<ActionResult<FriendRequestResponse>> GetRequest(Guid id, CancellationToken ct) =>
        FromResult(await friends.GetRequestAsync(CurrentUserId, id, ct));

    [HttpPost("requests")]
    [ProducesResponseType<FriendRequestResponse>(StatusCodes.Status201Created)]
    [ProducesResponseType<ProblemDetails>(StatusCodes.Status400BadRequest)]
    [ProducesResponseType<ProblemDetails>(StatusCodes.Status404NotFound)]
    [ProducesResponseType<ProblemDetails>(StatusCodes.Status409Conflict)]
    public async Task<ActionResult<FriendRequestResponse>> SendRequest(SendFriendRequestRequest request, CancellationToken ct)
    {
        var result = await friends.SendRequestAsync(CurrentUserId, request.UserName, ct);
        return result.Value is { } created
            ? CreatedAtAction(nameof(GetRequest), new { id = created.Id }, created)
            : FromResult(result);
    }

    [HttpPost("requests/{id:guid}/accept")]
    [ProducesResponseType<FriendRequestResponse>(StatusCodes.Status200OK)]
    [ProducesResponseType<ProblemDetails>(StatusCodes.Status404NotFound)]
    [ProducesResponseType<ProblemDetails>(StatusCodes.Status409Conflict)]
    public async Task<ActionResult<FriendRequestResponse>> Accept(Guid id, CancellationToken ct) =>
        FromResult(await friends.AcceptAsync(CurrentUserId, id, ct));

    [HttpPost("requests/{id:guid}/decline")]
    [ProducesResponseType<FriendRequestResponse>(StatusCodes.Status200OK)]
    [ProducesResponseType<ProblemDetails>(StatusCodes.Status404NotFound)]
    [ProducesResponseType<ProblemDetails>(StatusCodes.Status409Conflict)]
    public async Task<ActionResult<FriendRequestResponse>> Decline(Guid id, CancellationToken ct) =>
        FromResult(await friends.DeclineAsync(CurrentUserId, id, ct));

    /// <summary>Withdraws a pending request the current user sent.</summary>
    [HttpPost("requests/{id:guid}/cancel")]
    [ProducesResponseType<FriendRequestResponse>(StatusCodes.Status200OK)]
    [ProducesResponseType<ProblemDetails>(StatusCodes.Status404NotFound)]
    [ProducesResponseType<ProblemDetails>(StatusCodes.Status409Conflict)]
    public async Task<ActionResult<FriendRequestResponse>> Cancel(Guid id, CancellationToken ct) =>
        FromResult(await friends.CancelAsync(CurrentUserId, id, ct));
}
