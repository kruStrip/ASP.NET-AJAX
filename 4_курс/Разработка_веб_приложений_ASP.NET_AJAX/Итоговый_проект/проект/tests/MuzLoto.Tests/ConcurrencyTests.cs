using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using MuzLoto.Web.Data;
using MuzLoto.Web.Domain;
using MuzLoto.Web.Services;
using Xunit;

namespace MuzLoto.Tests;

/// <summary>Specification scenario 4: two staff members confirm the last seats at the same moment — no overselling.</summary>
[Trait("Category", "Integration")]
[Collection(AppCollection.Name)]
public sealed class ConcurrencyTests(AppFactory app)
{
    [Fact]
    public async Task SimultaneousConfirmations_NeverOversell_AndLoserGetsClearMessage()
    {
        var staff = await app.CreateUserAsync("Admin");
        for (var round = 0; round < 20; round++)
        {
            Guid sessionId;
            using (var scope = app.Services.CreateScope())
            {
                var db = scope.ServiceProvider.GetRequiredService<AppDbContext>();
                var session = new GameSession
                {
                    Game = new Game { Title = "Гонка " + round }, StartsAt = DateTimeOffset.UtcNow.AddDays(2),
                    TimeZoneId = "Etc/UTC", Venue = "Зал", TicketPrice = 500m, Capacity = 3
                };
                db.GameSessions.Add(session);
                await db.SaveChangesAsync();
                sessionId = session.Id;
            }
            var ids = new List<Guid>();
            foreach (var team in new[] { "А", "Б" })
                using (var scope = app.Services.CreateScope())
                    ids.Add((await scope.ServiceProvider.GetRequiredService<BookingWorkflow>().ReceiveAsync(new IncomingBooking(
                        "race-" + Guid.NewGuid().ToString("N"), sessionId, team, "Клиент", "+79000000020", null, 2))).Id);

            // Each confirmation runs in its own request scope, exactly like two staff members' browsers.
            var results = await Task.WhenAll(ids.Select(async id =>
            {
                using var scope = app.Services.CreateScope();
                try { await scope.ServiceProvider.GetRequiredService<BookingWorkflow>().ConfirmAsync(id, staff.Id); return (Exception?)null; }
                catch (Exception ex) { return ex; }
            }));

            Assert.Equal(1, results.Count(r => r is null));
            var loser = Assert.IsType<BusinessException>(results.Single(r => r is not null));
            Assert.Equal(409, loser.StatusCode);
            using var check = app.Services.CreateScope();
            var sold = await check.ServiceProvider.GetRequiredService<AppDbContext>().Bookings
                .Where(x => x.GameSessionId == sessionId && x.Status == BookingStatus.Confirmed).SumAsync(x => x.Quantity);
            Assert.Equal(2, sold);   // capacity 3: two orders of 2 can never both be confirmed
        }
    }
}
