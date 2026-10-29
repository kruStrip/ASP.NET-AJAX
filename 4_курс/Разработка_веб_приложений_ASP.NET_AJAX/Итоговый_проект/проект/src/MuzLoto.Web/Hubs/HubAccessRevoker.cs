using Microsoft.AspNetCore.SignalR;

namespace MuzLoto.Web.Hubs;

/// <summary>Removes a user's live connections from game groups right after their access is taken away.</summary>
public sealed class HubAccessRevoker(IHubContext<BookingHub> hub, HubConnectionTracker tracker, ILogger<HubAccessRevoker> logger)
{
    /// <param name="sessionId">One game, or every joined game when null (role change).</param>
    public async Task RevokeAsync(string userId, Guid? sessionId = null)
    {
        foreach (var (connectionId, joined) in tracker.MembershipsOf(userId, sessionId))
        {
            try
            {
                await hub.Groups.RemoveFromGroupAsync(connectionId, BookingHub.GroupFor(joined));
                tracker.Left(connectionId, joined);
                await hub.Clients.Client(connectionId).SendAsync("AccessRevoked", joined);
            }
            catch (Exception ex)
            {
                // The database change is already committed; a failed notification must not undo it.
                logger.LogWarning(ex, "Could not revoke hub group {SessionId} for connection {ConnectionId}", joined, connectionId);
            }
        }
    }
}
