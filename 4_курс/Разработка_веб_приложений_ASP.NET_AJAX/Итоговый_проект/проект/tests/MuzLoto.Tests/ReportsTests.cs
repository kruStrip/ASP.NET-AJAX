using System.Net;
using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Text.Json;
using Microsoft.Extensions.DependencyInjection;
using MuzLoto.Web.Data;
using MuzLoto.Web.Domain;
using MuzLoto.Web.Services;
using Xunit;

namespace MuzLoto.Tests;

/// <summary>Stage 8c: sales by game, by date and the operational report (specification 6.4).</summary>
[Trait("Category", "Integration")]
[Collection(AppCollection.Name)]
public sealed class ReportsTests(AppFactory app)
{
    [Fact]
    public async Task ByGame_ByDate_Operational_CountOnlyPaidNetOfRefunds()
    {
        var manager = await app.CreateUserAsync("Manager");
        var tag = Guid.NewGuid().ToString("N")[..6];
        var gameA = new Game { Title = "Отчёт А " + tag };
        var gameB = new Game { Title = "Отчёт Б " + tag };
        var a1 = new GameSession { Game = gameA, StartsAt = new DateTimeOffset(2031, 3, 10, 10, 0, 0, TimeSpan.Zero), TimeZoneId = "Etc/UTC", Venue = "Зал", TicketPrice = 700m, Capacity = 20 };
        var a2 = new GameSession { Game = gameA, StartsAt = new DateTimeOffset(2031, 4, 10, 10, 0, 0, TimeSpan.Zero), TimeZoneId = "Etc/UTC", Venue = "Зал", TicketPrice = 700m, Capacity = 20 };
        var b1 = new GameSession { Game = gameB, StartsAt = new DateTimeOffset(2031, 3, 20, 10, 0, 0, TimeSpan.Zero), TimeZoneId = "Etc/UTC", Venue = "Зал", TicketPrice = 500m, Capacity = 20 };
        using (var scope = app.Services.CreateScope())
        {
            var db = scope.ServiceProvider.GetRequiredService<AppDbContext>();
            db.GameSessions.AddRange(a1, a2, b1);
            db.EventStaffAssignments.AddRange(new[] { a1, a2, b1 }.Select(s => new EventStaffAssignment { UserId = manager.Id, GameSession = s }));
            await db.SaveChangesAsync();
        }

        async Task<Guid> PaidAsync(GameSession s, int qty)
        {
            using var scope = app.Services.CreateScope();
            var wf = scope.ServiceProvider.GetRequiredService<BookingWorkflow>();
            var (id, _) = await wf.ReceiveAsync(new IncomingBooking("r-" + Guid.NewGuid().ToString("N"), s.Id, "К", "Клиент", "+79000000030", null, qty));
            await wf.ConfirmAsync(id, manager.Id);
            return id;
        }
        await PaidAsync(a1, 4);                       // 2800
        var reduced = await PaidAsync(a2, 3);          // 2100, later reduced to 2 with 700 refunded
        await PaidAsync(b1, 2);                       // 1000
        using (var scope = app.Services.CreateScope())
        {
            var wf = scope.ServiceProvider.GetRequiredService<BookingWorkflow>();
            await wf.ChangePaidQuantityAsync(reduced, 2, manager.Id);
            var db = scope.ServiceProvider.GetRequiredService<AppDbContext>();
            var refund = db.Refunds.Single(x => x.Payment.BookingId == reduced);
            await wf.ConfirmRefundAsync(refund.Id, manager.Id);
            await wf.ReceiveAsync(new IncomingBooking("r-" + Guid.NewGuid().ToString("N"), b1.Id, "Ждут", "Клиент", "+79000000031", null, 5)); // pending: not a sale
        }

        var client = app.Browser();
        var login = await client.PostAsJsonAsync("/api/auth/login", new { email = manager.Email, password = "Secret123" });
        client.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer",
            (await login.Content.ReadFromJsonAsync<JsonElement>()).GetProperty("accessToken").GetString());

        var byGame = await client.GetFromJsonAsync<JsonElement[]>("/api/v1/reports/by-game");
        var rowA = byGame!.Single(x => x.GetProperty("title").GetString() == gameA.Title);
        Assert.Equal((6, 4200m, 2), (rowA.GetProperty("tickets").GetInt32(), rowA.GetProperty("revenue").GetDecimal(), rowA.GetProperty("orders").GetInt32()));
        var rowB = byGame.Single(x => x.GetProperty("title").GetString() == gameB.Title);
        Assert.Equal((2, 1000m, 1), (rowB.GetProperty("tickets").GetInt32(), rowB.GetProperty("revenue").GetDecimal(), rowB.GetProperty("orders").GetInt32()));
        Assert.Equal(gameA.Title, byGame[0].GetProperty("title").GetString());     // sorted by revenue

        var byDate = await client.GetFromJsonAsync<JsonElement[]>("/api/v1/reports/by-date");
        Assert.Equal(new[] { a1.Id, b1.Id, a2.Id }, byDate!.Select(x => x.GetProperty("sessionId").GetGuid()).ToArray());
        Assert.Equal(1400m, byDate.Single(x => x.GetProperty("sessionId").GetGuid() == a2.Id).GetProperty("revenue").GetDecimal());

        var march = await client.GetFromJsonAsync<JsonElement[]>("/api/v1/reports/by-date?from=2031-03-01&to=2031-03-31");
        Assert.Equal(new[] { a1.Id, b1.Id }, march!.Select(x => x.GetProperty("sessionId").GetGuid()).ToArray());

        var operational = await client.GetFromJsonAsync<JsonElement[]>("/api/v1/reports/operational");
        var opB = operational!.Single(x => x.GetProperty("sessionId").GetGuid() == b1.Id);
        Assert.Equal((2, 18, 1, 5), (opB.GetProperty("sold").GetInt32(), opB.GetProperty("remaining").GetInt32(),
            opB.GetProperty("pendingRequests").GetInt32(), opB.GetProperty("pendingTickets").GetInt32()));

        var admin = await app.SignedInAsync(await app.CreateUserAsync("Admin"));
        var page = WebUtility.HtmlDecode(await admin.GetStringAsync("/reports?from=2031-03-01&to=2031-03-31"));
        Assert.Contains(gameA.Title, page);
        Assert.Contains("10.03.2031", page);
        Assert.DoesNotContain("10.04.2031", page.Split("Оперативный отчёт")[0]);   // filtered out of the sales tables
    }
}
