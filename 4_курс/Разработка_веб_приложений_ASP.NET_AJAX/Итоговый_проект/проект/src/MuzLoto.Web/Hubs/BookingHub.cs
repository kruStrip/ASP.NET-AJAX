using System.Security.Claims;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.SignalR;
using MuzLoto.Web.Services;

namespace MuzLoto.Web.Hubs;

/// <summary>
/// Real-time booking events for staff. Every JoinSession re-checks the role and the game assignment,
/// so a reconnecting client (new connection) never keeps rights it has lost.
/// </summary>
[Authorize(Roles = "Admin,Manager")]
public class BookingHub(StaffAccess access, HubConnectionTracker tracker, ILogger<BookingHub> logger) : Hub
{
    public static string GroupFor(Guid sessionId) => $"session:{sessionId}";

    public override async Task OnConnectedAsync()
    {
        var userId = Context.User!.FindFirstValue(ClaimTypes.NameIdentifier)!;
        tracker.Connected(Context.ConnectionId, userId);
        logger.LogInformation("BookingHub: {User} connected ({ConnectionId}); online staff: {Online}",
            Context.User!.Identity?.Name, Context.ConnectionId, tracker.OnlineUserCount);
        await Clients.All.SendAsync("OnlineCountChanged", tracker.OnlineUserCount);
        await base.OnConnectedAsync();
    }

    public override async Task OnDisconnectedAsync(Exception? exception)
    {
        tracker.Disconnected(Context.ConnectionId);
        logger.LogInformation(exception, "BookingHub: {User} disconnected ({ConnectionId}); online staff: {Online}",
            Context.User?.Identity?.Name, Context.ConnectionId, tracker.OnlineUserCount);
        await Clients.All.SendAsync("OnlineCountChanged", tracker.OnlineUserCount);
        await base.OnDisconnectedAsync(exception);
    }

    public async Task JoinSession(Guid sessionId)
    {
        if (!await access.CanManageAsync(Context.User!, sessionId))
        {
            logger.LogWarning("BookingHub: {User} denied group for session {SessionId}", Context.User!.Identity?.Name, sessionId);
            throw new HubException("Нет доступа к мероприятию.");
        }
        await Groups.AddToGroupAsync(Context.ConnectionId, GroupFor(sessionId));
        tracker.Joined(Context.ConnectionId, sessionId);
    }

    public async Task LeaveSession(Guid sessionId)
    {
        await Groups.RemoveFromGroupAsync(Context.ConnectionId, GroupFor(sessionId));
        tracker.Left(Context.ConnectionId, sessionId);
    }
}
