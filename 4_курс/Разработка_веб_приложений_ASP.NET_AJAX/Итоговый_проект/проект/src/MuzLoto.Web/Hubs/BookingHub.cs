using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.SignalR;
using MuzLoto.Web.Services;

namespace MuzLoto.Web.Hubs;

[Authorize(Roles = "Admin,Manager")]
public class BookingHub(StaffAccess access) : Hub
{
    public async Task JoinSession(Guid sessionId)
    {
        if (!await access.CanManageAsync(Context.User!, sessionId))
            throw new HubException("Нет доступа к мероприятию.");
        await Groups.AddToGroupAsync(Context.ConnectionId, $"session:{sessionId}");
    }
    public Task LeaveSession(Guid sessionId) =>
        Groups.RemoveFromGroupAsync(Context.ConnectionId, $"session:{sessionId}");
}
