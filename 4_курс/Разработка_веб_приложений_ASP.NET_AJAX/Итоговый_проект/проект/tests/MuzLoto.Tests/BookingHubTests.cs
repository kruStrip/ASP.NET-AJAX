using Microsoft.AspNetCore.Http.Connections;
using Microsoft.AspNetCore.SignalR;
using Microsoft.AspNetCore.SignalR.Client;
using Microsoft.Extensions.DependencyInjection;
using MuzLoto.Web.Data;
using MuzLoto.Web.Domain;
using MuzLoto.Web.Hubs;
using MuzLoto.Web.Services;
using Xunit;

namespace MuzLoto.Tests;

/// <summary>Real SignalR connections (long polling over the test server) authenticated with a staff cookie.</summary>
[Trait("Category", "Integration")]
[Collection(AppCollection.Name)]
public sealed class BookingHubTests(AppFactory app)
{
    private static readonly TimeSpan Wait = TimeSpan.FromSeconds(10);

    private async Task<HubConnection> ConnectAsync(AppUser user)
    {
        var cookie = await app.CookieHeaderAsync(user);
        var connection = new HubConnectionBuilder()
            .WithUrl(new Uri(app.Server.BaseAddress, "/hubs/bookings"), o =>
            {
                o.HttpMessageHandlerFactory = _ => app.Server.CreateHandler();
                o.Transports = HttpTransportType.LongPolling;
                o.Headers["Cookie"] = cookie;
            })
            .Build();
        await connection.StartAsync();
        return connection;
    }

    private async Task<(Guid SessionId, Guid BookingId)> CreatePendingAsync(string? assignedManagerId = null)
    {
        using var scope = app.Services.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<AppDbContext>();
        var session = new GameSession
        {
            Game = new Game { Title = "Хаб " + Guid.NewGuid().ToString("N")[..8] },
            StartsAt = DateTimeOffset.UtcNow.AddDays(3), TimeZoneId = "Etc/UTC",
            Venue = "Зал", TicketPrice = 500m, Capacity = 10
        };
        var booking = new Booking
        {
            GameSession = session, TeamName = "Команда", CustomerName = "Клиент",
            Phone = "+79000000002", Quantity = 1, TicketPriceSnapshot = 500m
        };
        db.Bookings.Add(booking);
        if (assignedManagerId is not null)
            db.EventStaffAssignments.Add(new EventStaffAssignment { UserId = assignedManagerId, GameSession = session });
        await db.SaveChangesAsync();
        return (session.Id, booking.Id);
    }

    private async Task ConfirmAsync(Guid bookingId, string staffUserId)
    {
        using var scope = app.Services.CreateScope();
        await scope.ServiceProvider.GetRequiredService<BookingWorkflow>().ConfirmAsync(bookingId, staffUserId);
    }

    [Fact]
    public async Task AnonymousConnection_IsRejected()
    {
        var connection = new HubConnectionBuilder()
            .WithUrl(new Uri(app.Server.BaseAddress, "/hubs/bookings"), o =>
            {
                o.HttpMessageHandlerFactory = _ => app.Server.CreateHandler();
                o.Transports = HttpTransportType.LongPolling;
            })
            .Build();
        await Assert.ThrowsAnyAsync<Exception>(() => connection.StartAsync());
    }

    [Fact]
    public async Task AssignedManager_GetsGroupEvents_UnassignedGameIsDenied()
    {
        var manager = await app.CreateUserAsync("Manager");
        var (sessionId, bookingId) = await CreatePendingAsync(manager.Id);
        var (otherSessionId, _) = await CreatePendingAsync();
        await using var connection = await ConnectAsync(manager);

        var confirmed = new TaskCompletionSource<Guid>(TaskCreationOptions.RunContinuationsAsynchronously);
        connection.On<Guid>("BookingConfirmed", id => confirmed.TrySetResult(id));
        await connection.InvokeAsync("JoinSession", sessionId);
        await Assert.ThrowsAsync<HubException>(() => connection.InvokeAsync("JoinSession", otherSessionId));

        await ConfirmAsync(bookingId, manager.Id);
        Assert.Equal(bookingId, await confirmed.Task.WaitAsync(Wait));
    }

    [Fact]
    public async Task RemovingAssignment_DropsLiveConnectionFromGroup()
    {
        var manager = await app.CreateUserAsync("Manager");
        var (sessionId, bookingId) = await CreatePendingAsync(manager.Id);
        await using var connection = await ConnectAsync(manager);
        var revoked = new TaskCompletionSource<Guid>(TaskCreationOptions.RunContinuationsAsynchronously);
        var leaked = new TaskCompletionSource<Guid>(TaskCreationOptions.RunContinuationsAsynchronously);
        connection.On<Guid>("AccessRevoked", id => revoked.TrySetResult(id));
        connection.On<Guid>("BookingConfirmed", id => leaked.TrySetResult(id));
        await connection.InvokeAsync("JoinSession", sessionId);

        using (var scope = app.Services.CreateScope())
            await scope.ServiceProvider.GetRequiredService<StaffManagement>().RemoveManagerAsync(sessionId, manager.Id);
        Assert.Equal(sessionId, await revoked.Task.WaitAsync(Wait));

        await ConfirmAsync(bookingId, manager.Id);
        await Task.Delay(TimeSpan.FromSeconds(1));
        Assert.False(leaked.Task.IsCompleted, "Revoked connection still received group events.");
        await Assert.ThrowsAsync<HubException>(() => connection.InvokeAsync("JoinSession", sessionId));
    }

    [Fact]
    public async Task ConnectAndDisconnect_UpdateOnlineCount()
    {
        var tracker = app.Services.GetRequiredService<HubConnectionTracker>();
        var admin = await app.CreateUserAsync("Admin");
        var before = tracker.ConnectionCount;
        var expectedUsers = tracker.OnlineUserCount + 2;
        var connection = await ConnectAsync(admin);
        var observed = new TaskCompletionSource<int>(TaskCreationOptions.RunContinuationsAsynchronously);
        connection.On<int>("OnlineCountChanged", n => { if (n == expectedUsers) observed.TrySetResult(n); });
        Assert.Equal(before + 1, tracker.ConnectionCount);

        // The first connection hears that a second staff member came online.
        await using var second = await ConnectAsync(await app.CreateUserAsync("Manager"));
        Assert.Equal(expectedUsers, await observed.Task.WaitAsync(Wait));

        await connection.DisposeAsync();
        for (var i = 0; i < 50 && tracker.ConnectionCount != before + 1; i++) await Task.Delay(100);
        Assert.Equal(before + 1, tracker.ConnectionCount);
    }
}
