using System.Net;
using System.Text.RegularExpressions;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using MuzLoto.Web.Data;
using MuzLoto.Web.Domain;
using MuzLoto.Web.Services;
using Xunit;

namespace MuzLoto.Tests;

/// <summary>Stage 8b: ticket-count changes on paid orders (partial refund / surcharge), search and sales history.</summary>
[Trait("Category", "Integration")]
[Collection(AppCollection.Name)]
public sealed class PaidQuantityTests(AppFactory app)
{
    private async Task<T> WithAsync<T>(Func<BookingWorkflow, AppDbContext, Task<T>> action)
    {
        using var scope = app.Services.CreateScope();
        return await action(scope.ServiceProvider.GetRequiredService<BookingWorkflow>(), scope.ServiceProvider.GetRequiredService<AppDbContext>());
    }

    private async Task<(Guid SessionId, Guid BookingId, string StaffId)> PaidOrderAsync(int capacity, int quantity, string phone = "+79000000010")
    {
        var staff = await app.CreateUserAsync("Admin");
        var sessionId = await WithAsync(async (_, db) =>
        {
            var s = new GameSession
            {
                Game = new Game { Title = "Количество " + Guid.NewGuid().ToString("N")[..6] },
                StartsAt = DateTimeOffset.UtcNow.AddDays(5), TimeZoneId = "Etc/UTC", Venue = "Зал",
                TicketPrice = 700m, Capacity = capacity
            };
            db.GameSessions.Add(s);
            await db.SaveChangesAsync();
            return s.Id;
        });
        var bookingId = await WithAsync(async (wf, _) =>
        {
            var (id, _) = await wf.ReceiveAsync(new IncomingBooking("q-" + Guid.NewGuid().ToString("N"), sessionId,
                "Команда " + Guid.NewGuid().ToString("N")[..6], "Клиент", phone, null, quantity));
            await wf.ConfirmAsync(id, staff.Id);
            return id;
        });
        return (sessionId, bookingId, staff.Id);
    }

    private Task<int> SoldAsync(Guid sessionId) => WithAsync((_, db) =>
        db.Bookings.Where(x => x.GameSessionId == sessionId && x.Status == BookingStatus.Confirmed).SumAsync(x => x.Quantity));

    private Task<List<Refund>> RefundsAsync(Guid bookingId) => WithAsync((_, db) =>
        db.Refunds.AsNoTracking().Where(x => x.Payment.BookingId == bookingId).OrderBy(x => x.RequestedAt).ToListAsync());

    [Fact]
    public async Task Decrease_FreesSeatsAndRefundsDifference_ThenCancellationRefundsOnlyTheRest()
    {
        var (sessionId, bookingId, staff) = await PaidOrderAsync(capacity: 10, quantity: 5);
        await WithAsync((wf, _) => wf.ChangePaidQuantityAsync(bookingId, 3, staff));

        Assert.Equal(3, await SoldAsync(sessionId));                         // seats freed at once
        var partial = Assert.Single(await RefundsAsync(bookingId));
        Assert.Equal((RefundKind.QuantityDecrease, RefundStatus.Pending, 1400m), (partial.Kind, partial.Status, partial.Amount));
        Assert.Equal(3500m, await WithAsync((_, db) => db.Payments.Where(x => x.BookingId == bookingId).Select(x => x.Amount).SingleAsync()));

        // A partial refund can be paid out while the order stays active.
        await WithAsync(async (wf, _) => { await wf.ConfirmRefundAsync(partial.Id, staff); return 0; });
        Assert.Equal(RefundStatus.Completed, (await RefundsAsync(bookingId)).Single().Status);

        await WithAsync(async (wf, _) => { await wf.CancelAsync(bookingId, staff); return 0; });
        var cancellation = (await RefundsAsync(bookingId)).Single(x => x.Kind == RefundKind.Cancellation);
        Assert.Equal(2100m, cancellation.Amount);                             // 3500 paid − 1400 already returned
        Assert.Equal(0, await SoldAsync(sessionId));

        // The earlier partial refund does not block restoring a cancelled order within the hour.
        await WithAsync(async (wf, _) => { await wf.RestoreCancelledAsync(bookingId, staff); return 0; });
        Assert.Equal(3, await SoldAsync(sessionId));
        Assert.Equal(RefundStatus.Cancelled, (await RefundsAsync(bookingId)).Single(x => x.Kind == RefundKind.Cancellation).Status);
    }

    [Fact]
    public async Task Increase_WaitsForSurcharge_ThenTakesSeatsAndGrowsPayment()
    {
        var (sessionId, bookingId, staff) = await PaidOrderAsync(capacity: 6, quantity: 4);
        var change = (await WithAsync((wf, _) => wf.ChangePaidQuantityAsync(bookingId, 6, staff)))!;
        Assert.Equal(QuantityChangeStatus.AwaitingPayment, change.Status);
        Assert.Equal(1400m, change.Amount);
        Assert.Equal(4, await SoldAsync(sessionId));                         // nothing taken before the money

        var second = await Assert.ThrowsAsync<BusinessException>(() => WithAsync((wf, _) => wf.ChangePaidQuantityAsync(bookingId, 5, staff)));
        Assert.Equal(409, second.StatusCode);

        await WithAsync(async (wf, _) => { await wf.ConfirmSurchargeAsync(change.Id, staff); return 0; });
        Assert.Equal(6, await SoldAsync(sessionId));
        Assert.Equal(4200m, await WithAsync((_, db) => db.Payments.Where(x => x.BookingId == bookingId).Select(x => x.Amount).SingleAsync()));
        await WithAsync(async (wf, _) => { await wf.ConfirmSurchargeAsync(change.Id, staff); return 0; });   // idempotent
        Assert.Equal(4200m, await WithAsync((_, db) => db.Payments.Where(x => x.BookingId == bookingId).Select(x => x.Amount).SingleAsync()));

        var full = await Assert.ThrowsAsync<BusinessException>(() => WithAsync((wf, _) => wf.ChangePaidQuantityAsync(bookingId, 7, staff)));
        Assert.Contains("Свободных мест недостаточно", full.Message);
    }

    [Fact]
    public async Task Surcharge_FailsIfSeatsWereSoldMeanwhile_AndCancellationDropsIt()
    {
        var (sessionId, bookingId, staff) = await PaidOrderAsync(capacity: 5, quantity: 3);
        var change = (await WithAsync((wf, _) => wf.ChangePaidQuantityAsync(bookingId, 5, staff)))!;
        await WithAsync(async (wf, _) =>
        {
            var (other, _) = await wf.ReceiveAsync(new IncomingBooking("q-" + Guid.NewGuid().ToString("N"), sessionId, "Другие", "Клиент", "+79000000011", null, 1));
            await wf.ConfirmAsync(other, staff);
            return 0;
        });
        var denied = await Assert.ThrowsAsync<BusinessException>(() => WithAsync(async (wf, _) => { await wf.ConfirmSurchargeAsync(change.Id, staff); return 0; }));
        Assert.Equal(409, denied.StatusCode);
        Assert.Equal(4, await SoldAsync(sessionId));                        // no overselling

        await WithAsync(async (wf, _) => { await wf.CancelAsync(bookingId, staff); return 0; });
        Assert.Equal(QuantityChangeStatus.Cancelled,
            await WithAsync((_, db) => db.QuantityChanges.Where(x => x.Id == change.Id).Select(x => x.Status).SingleAsync()));
        Assert.Equal(2100m, (await RefundsAsync(bookingId)).Single().Amount);   // surcharge was never paid
    }

    [Fact]
    public async Task Ajax_QuantityChanges_OnPendingAndPaidPages_ZeroCancels()
    {
        var admin = await app.SignedInAsync(await app.CreateUserAsync("Admin"));
        var page = await admin.GetStringAsync("/bookings");
        var token = Regex.Match(page, "<meta name=\"csrf-token\" content=\"([^\"]+)\"").Groups[1].Value;
        async Task<HttpResponseMessage> AjaxAsync(string url, Dictionary<string, string> form)
        {
            var request = new HttpRequestMessage(HttpMethod.Post, url) { Content = new FormUrlEncodedContent(form) };
            request.Headers.Add("X-Requested-With", "XMLHttpRequest");
            request.Headers.Add("X-CSRF-TOKEN", token);
            return await admin.SendAsync(request);
        }

        var (sessionId, paidId, _) = await PaidOrderAsync(capacity: 10, quantity: 4);
        var pendingId = await WithAsync(async (wf, _) =>
            (await wf.ReceiveAsync(new IncomingBooking("q-" + Guid.NewGuid().ToString("N"), sessionId, "Ждут", "Клиент", "+79000000012", null, 2))).Id);

        var pending = await AjaxAsync("/bookings/pending?handler=Quantity", new() { ["id"] = pendingId.ToString(), ["quantity"] = "3" });
        Assert.Equal(HttpStatusCode.OK, pending.StatusCode);
        var message = System.Text.Json.JsonDocument.Parse(await pending.Content.ReadAsStringAsync())
            .RootElement.GetProperty("message").GetString()!;
        Assert.Contains("2 100", new string(message.Select(c => char.IsWhiteSpace(c) ? ' ' : c).ToArray()));
        Assert.Equal(3, await WithAsync((_, db) => db.Bookings.Where(x => x.Id == pendingId).Select(x => x.Quantity).SingleAsync()));

        var tooMany = await AjaxAsync("/bookings/pending?handler=Quantity", new() { ["id"] = pendingId.ToString(), ["quantity"] = "11" });
        Assert.Equal(HttpStatusCode.Conflict, tooMany.StatusCode);

        var reduce = await AjaxAsync("/bookings?handler=Quantity", new() { ["id"] = paidId.ToString(), ["quantity"] = "2" });
        Assert.Equal(HttpStatusCode.OK, reduce.StatusCode);
        Assert.Equal(2, await SoldAsync(sessionId));

        var zero = await AjaxAsync("/bookings?handler=Quantity", new() { ["id"] = paidId.ToString(), ["quantity"] = "0" });
        Assert.Equal(HttpStatusCode.OK, zero.StatusCode);
        Assert.Equal(BookingStatus.Cancelled, await WithAsync((_, db) => db.Bookings.Where(x => x.Id == paidId).Select(x => x.Status).SingleAsync()));
    }

    [Fact]
    public async Task Search_ByPhoneTeamOrId_AndHistoryKeepsPastGames()
    {
        var phone = "+7900" + Random.Shared.Next(1000000, 9999999);
        var (sessionId, bookingId, staff) = await PaidOrderAsync(capacity: 10, quantity: 2, phone: phone);
        var team = await WithAsync((_, db) => db.Bookings.Where(x => x.Id == bookingId).Select(x => x.TeamName).SingleAsync());
        var admin = await app.SignedInAsync(await app.CreateUserAsync("Admin"));

        var formatted = $"8 ({phone[2..5]}) {phone[5..8]}-{phone[8..10]}-{phone[10..]}";
        foreach (var query in new[] { formatted, team, bookingId.ToString()[..8] })
            Assert.Contains(team, WebUtility.HtmlDecode(await admin.GetStringAsync($"/bookings?q={Uri.EscapeDataString(query)}")));
        Assert.DoesNotContain(team, WebUtility.HtmlDecode(await admin.GetStringAsync("/bookings?q=нет-такой-команды")));

        await WithAsync(async (wf, _) => { await wf.CompleteSessionAsync(sessionId); return 0; });
        Assert.DoesNotContain(team, WebUtility.HtmlDecode(await admin.GetStringAsync("/bookings")));   // active list
        var history = WebUtility.HtmlDecode(await admin.GetStringAsync($"/bookings/history?q={Uri.EscapeDataString(team)}"));
        Assert.Contains(team, history);
        Assert.Contains("Игра прошла", history);
    }
}
