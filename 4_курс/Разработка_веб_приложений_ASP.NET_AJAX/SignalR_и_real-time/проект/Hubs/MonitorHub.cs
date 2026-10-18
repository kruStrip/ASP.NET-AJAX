using System.Runtime.CompilerServices;
using LiveMonitor.Services;
using Microsoft.AspNetCore.SignalR;

namespace LiveMonitor.Hubs;

public sealed class MonitorHub(ConnectionTracker tracker) : Hub
{
    public override async Task OnConnectedAsync()
    {
        await tracker.RegisterAsync(Context.ConnectionId);
        await Clients.Others.SendAsync("UserConnected", Context.ConnectionId);
        await base.OnConnectedAsync();
    }

    public override async Task OnDisconnectedAsync(Exception? exception)
    {
        await tracker.RemoveAsync(Context.ConnectionId);
        await Clients.All.SendAsync("UserDisconnected", Context.ConnectionId);
        await base.OnDisconnectedAsync(exception);
    }

    public Task SendBroadcast(string text) =>
        Clients.All.SendAsync("ReceiveBroadcast", Context.ConnectionId, RequireText(text));

    public async Task JoinRoom(string room)
    {
        room = RequireRoom(room);
        var previousRoom = tracker.GetRoom(Context.ConnectionId);
        if (!string.IsNullOrWhiteSpace(previousRoom) && previousRoom != room)
            await Groups.RemoveFromGroupAsync(Context.ConnectionId, previousRoom);

        await Groups.AddToGroupAsync(Context.ConnectionId, room);
        await tracker.UpdateRoomAsync(Context.ConnectionId, room);
        await Clients.Caller.SendAsync("RoomJoined", room);
    }

    public async Task LeaveRoom(string room)
    {
        room = RequireRoom(room);
        await Groups.RemoveFromGroupAsync(Context.ConnectionId, room);
        if (tracker.GetRoom(Context.ConnectionId) == room)
            await tracker.UpdateRoomAsync(Context.ConnectionId, null);
        await Clients.Caller.SendAsync("RoomLeft", room);
    }

    public Task SendRoomMessage(string room, string text) =>
        Clients.Group(RequireRoom(room)).SendAsync("ReceiveRoomMessage", Context.ConnectionId, RequireText(text));

    public async Task SendPrivateMessage(string connectionId, string text)
    {
        await Clients.Client(RequireText(connectionId)).SendAsync("ReceivePrivateMessage", Context.ConnectionId, RequireText(text));
        await Clients.Caller.SendAsync("PrivateMessageSent", connectionId);
    }

    public Task NotifyTyping(string room) =>
        Clients.OthersInGroup(RequireRoom(room)).SendAsync("UserTyping", Context.ConnectionId);

    public async IAsyncEnumerable<int> StreamNumbers(int maxValue, [EnumeratorCancellation] CancellationToken cancellationToken)
    {
        if (maxValue < 1)
            throw new HubException("Максимальное значение должно быть положительным.");

        while (!cancellationToken.IsCancellationRequested)
        {
            yield return Random.Shared.Next(1, maxValue + 1);
            await Task.Delay(TimeSpan.FromMilliseconds(500), cancellationToken);
        }
    }

    private static string RequireText(string? value) => string.IsNullOrWhiteSpace(value)
        ? throw new HubException("Текст не может быть пустым.")
        : value.Trim();

    private static string RequireRoom(string? room) => RequireText(room);
}
