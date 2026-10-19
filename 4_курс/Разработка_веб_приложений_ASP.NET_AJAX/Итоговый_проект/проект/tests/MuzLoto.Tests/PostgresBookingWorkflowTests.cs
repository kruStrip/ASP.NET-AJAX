using Microsoft.AspNetCore.SignalR;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using MuzLoto.Web.Data;
using MuzLoto.Web.Domain;
using MuzLoto.Web.Hubs;
using MuzLoto.Web.Services;
using Xunit;

namespace MuzLoto.Tests;

/// <summary>
/// Runs only when MUZLOTO_PG_TEST=1, against a disposable PostgreSQL database
/// with the app's InitialCreate migrations already applied.
/// </summary>
public sealed class PostgresBookingWorkflowTests
{
    [Fact]
    [Trait("Category", "Integration")]
    public async Task Pending_DoesNotReserve_ConfirmedDoes_CompletionRemovesPending()
    {
        if (Environment.GetEnvironmentVariable("MUZLOTO_PG_TEST") != "1")
            throw new InvalidOperationException("Set MUZLOTO_PG_TEST=1 and use an isolated test database.");

        var connection = Environment.GetEnvironmentVariable("ConnectionStrings__Default")
            ?? throw new InvalidOperationException("ConnectionStrings__Default must point at an isolated test DB.");
        var options = new DbContextOptionsBuilder<AppDbContext>().UseNpgsql(connection).Options;

        using var services = new ServiceCollection()
            .AddLogging()
            .AddSignalR()
            .Services.BuildServiceProvider();
        var hub = services.GetRequiredService<IHubContext<BookingHub>>();
        var logger = services.GetRequiredService<ILogger<BookingWorkflow>>();
        var configuration = new ConfigurationBuilder().AddInMemoryCollection(
            new Dictionary<string, string?> { ["Integration:MaxTicketsPerBooking"] = "10" }).Build();

        await using var db = new AppDbContext(options);
        var user = new AppUser
        {
            UserName = "test-" + Guid.NewGuid().ToString("N"),
            Email = "dev-" + Guid.NewGuid().ToString("N") + "@invalid.test"
        };
        var game = new Game { Title = "Integration test " + Guid.NewGuid().ToString("N") };
        var session = new GameSession
        {
            Game = game,
            StartsAt = DateTimeOffset.UtcNow.AddDays(1),
            TimeZoneId = "Etc/UTC",
            Venue = "Isolated CI room",
            TicketPrice = 1500m,
            Capacity = 1
        };
        db.Users.Add(user);
        db.GameSessions.Add(session);
        await db.SaveChangesAsync();

        var workflow = new BookingWorkflow(db, hub, logger, configuration);
        var first = await workflow.ReceiveAsync(new IncomingBooking(
            "ci-" + Guid.NewGuid().ToString("N"), session.Id,
            "Команда 1", "Тест 1", "+70000000001", null, 1));
        var secondExternalId = "ci-" + Guid.NewGuid().ToString("N");
        var second = await workflow.ReceiveAsync(new IncomingBooking(
            secondExternalId, session.Id,
            "Команда 2", "Тест 2", "+70000000002", null, 1));

        Assert.False(first.Duplicate);
        Assert.False(second.Duplicate);
        Assert.Equal(0, await db.Bookings.CountAsync(x =>
            x.GameSessionId == session.Id && x.Status == BookingStatus.Confirmed));
        await workflow.ConfirmAsync(first.Id, user.Id);
        Assert.Equal(1, await db.Bookings.CountAsync(x =>
            x.GameSessionId == session.Id && x.Status == BookingStatus.Confirmed));
        var denied = await Assert.ThrowsAsync<BusinessException>(
            () => workflow.ConfirmAsync(second.Id, user.Id));
        Assert.Equal(409, denied.StatusCode);

        // The completed event clears *unpaid* bookings but keeps the paid one.
        await workflow.CompleteSessionAsync(session.Id);
        Assert.True(await db.Bookings.AnyAsync(x => x.Id == first.Id));
        Assert.False(await db.Bookings.AnyAsync(x => x.Id == second.Id));
        Assert.Equal(1, await db.Payments.CountAsync(x => x.BookingId == first.Id));
        var replay = await workflow.ReceiveAsync(new IncomingBooking(
            secondExternalId, session.Id,
            "Команда 2", "Тест 2", "+70000000002", null, 1));
        Assert.True(replay.Duplicate);
    }
}
