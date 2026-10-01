using System.Collections.Concurrent;
using LiveMonitor.Hubs;
using Microsoft.AspNetCore.SignalR;

namespace LiveMonitor.Services;

public sealed class ConnectionTracker(IHubContext<MonitorHub> hubContext)
{
    private readonly ConcurrentDictionary<string, string?> _connections = new();

    public async Task RegisterAsync(string connectionId)
    {
        _connections[connectionId] = null;
        await PublishStateAsync();
    }

    public async Task RemoveAsync(string connectionId)
    {
        _connections.TryRemove(connectionId, out _);
        await PublishStateAsync();
    }

    public async Task UpdateRoomAsync(string connectionId, string? room)
    {
        _connections[connectionId] = room;
        await PublishStateAsync();
    }

    public string? GetRoom(string connectionId) =>
        _connections.TryGetValue(connectionId, out var room) ? room : null;

    public Task SendSystemMessageAsync(string text) =>
        hubContext.Clients.All.SendAsync("SystemMessage", text.Trim());

    public Task SendRoomMessageAsync(string room, string text) =>
        hubContext.Clients.Group(room.Trim()).SendAsync("ReceiveRoomMessage", "HTTP", text.Trim());

    public Task SendPrivateMessageAsync(string connectionId, string text) =>
        hubContext.Clients.Client(connectionId.Trim()).SendAsync("ReceivePrivateMessage", "HTTP", text.Trim());

    private Task PublishStateAsync()
    {
        var users = _connections
            .OrderBy(pair => pair.Key)
            .Select(pair => new ActiveUser(pair.Key, pair.Value ?? "без комнаты"))
            .ToArray();

        return Task.WhenAll(
            hubContext.Clients.All.SendAsync("OnlineCount", users.Length),
            hubContext.Clients.All.SendAsync("ActiveUsers", users));
    }
}

public sealed record ActiveUser(string ConnectionId, string Room);
