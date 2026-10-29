using System.Collections.Concurrent;

namespace MuzLoto.Web.Hubs;

/// <summary>
/// In-memory registry of live BookingHub connections: who is connected and which game groups each connection joined.
/// Lets the server count staff online and pull a user's connections out of groups when their access is revoked.
/// Single-instance only; a scaled-out deployment would need a shared backplane.
/// </summary>
public sealed class HubConnectionTracker
{
    private sealed record Entry(string UserId, ConcurrentDictionary<Guid, byte> Sessions);
    private readonly ConcurrentDictionary<string, Entry> connections = new();

    public void Connected(string connectionId, string userId) =>
        connections[connectionId] = new Entry(userId, new ConcurrentDictionary<Guid, byte>());

    public void Disconnected(string connectionId) => connections.TryRemove(connectionId, out _);

    public void Joined(string connectionId, Guid sessionId)
    {
        if (connections.TryGetValue(connectionId, out var entry)) entry.Sessions[sessionId] = 0;
    }

    public void Left(string connectionId, Guid sessionId)
    {
        if (connections.TryGetValue(connectionId, out var entry)) entry.Sessions.TryRemove(sessionId, out _);
    }

    public int ConnectionCount => connections.Count;
    public int OnlineUserCount => connections.Values.Select(x => x.UserId).Distinct().Count();

    /// <summary>The user's connections that joined the given game group, or any group when sessionId is null.</summary>
    public IReadOnlyList<(string ConnectionId, Guid SessionId)> MembershipsOf(string userId, Guid? sessionId = null) =>
        connections.Where(x => x.Value.UserId == userId)
            .SelectMany(x => x.Value.Sessions.Keys.Select(s => (x.Key, s)))
            .Where(x => sessionId is null || x.s == sessionId)
            .ToList();
}
