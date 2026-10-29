using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using MuzLoto.Web.Data;
using MuzLoto.Web.Domain;
using Xunit;

namespace MuzLoto.Tests;

/// <summary>FormDesigner webhook with values exactly as the live form produces them.</summary>
[Trait("Category", "Integration")]
[Collection(AppCollection.Name)]
public sealed class FormDesignerWebhookTests(AppFactory app)
{
    private const string Url = "/api/integrations/formdesigner/webhook";
    private static int dayOffset = Random.Shared.Next(0, 2_000_000); // wide range: the test database keeps earlier runs' sessions

    /// <summary>A unique far-future day per test so dates never collide with other tests' sessions.</summary>
    private static DateOnly NextDay() => new DateOnly(2035, 1, 1).AddDays(Interlocked.Add(ref dayOffset, 7));

    private async Task<GameSession> SessionAsync(DateTimeOffset startsAt, string tz = "Etc/UTC", string? title = null, int capacity = 10, bool open = true)
    {
        using var scope = app.Services.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<AppDbContext>();
        var s = new GameSession
        {
            Game = new Game { Title = title ?? "Вебхук " + Guid.NewGuid().ToString("N")[..6] },
            StartsAt = startsAt, TimeZoneId = tz, Venue = "Зал", TicketPrice = 700m, Capacity = capacity, SalesOpen = open
        };
        db.GameSessions.Add(s);
        await db.SaveChangesAsync();
        return s;
    }

    private static Dictionary<string, string> Form(DateOnly day, string? time = "19:00", string quantity = "3", string? submissionId = null)
    {
        var f = new Dictionary<string, string>
        {
            ["key"] = AppFactory.IntegrationSecret, ["game_date"] = day.ToString("dd.MM.yyyy"), ["quantity"] = quantity,
            ["name"] = "Анна", ["phone"] = "8 (900) 000-00-41", ["telegram"] = "@team_fd",
            ["team"] = "Команда " + Guid.NewGuid().ToString("N")[..6], ["total"] = "2100 р", ["game_name"] = "Хиты"
        };
        if (time is not null) f["game_time"] = time;
        if (submissionId is not null) f["submission_id"] = submissionId;
        return f;
    }

    private Task<HttpResponseMessage> PostFormAsync(Dictionary<string, string> fields) =>
        app.Browser().PostAsync(Url, new FormUrlEncodedContent(fields));

    private async Task<Booking> BookingAsync(HttpResponseMessage response)
    {
        var id = (await response.Content.ReadFromJsonAsync<JsonElement>()).GetProperty("bookingId").GetGuid();
        using var scope = app.Services.CreateScope();
        return await scope.ServiceProvider.GetRequiredService<AppDbContext>().Bookings.AsNoTracking().SingleAsync(x => x.Id == id);
    }

    [Fact]
    public async Task FormPost_CreatesPendingBooking_RetryWithSameIdIsNotDuplicated()
    {
        var day = NextDay();
        var session = await SessionAsync(new DateTimeOffset(day.ToDateTime(new TimeOnly(19, 0)), TimeSpan.Zero));
        var fields = Form(day, submissionId: "fd-" + Guid.NewGuid().ToString("N"));

        var first = await PostFormAsync(fields);
        Assert.Equal(HttpStatusCode.Created, first.StatusCode);
        var booking = await BookingAsync(first);
        Assert.Equal((session.Id, BookingStatus.Pending, 3, "+79000000041", "@team_fd", 700m),
            (booking.GameSessionId, booking.Status, booking.Quantity, booking.Phone, booking.Telegram, booking.TicketPriceSnapshot));

        var retry = await PostFormAsync(fields);
        Assert.Equal(HttpStatusCode.OK, retry.StatusCode);
        Assert.Equal(booking.Id, (await BookingAsync(retry)).Id);
    }

    [Fact]
    public async Task WithoutSubmissionId_IdenticalRetryIsRecognised()
    {
        var day = NextDay();
        await SessionAsync(new DateTimeOffset(day.ToDateTime(new TimeOnly(19, 0)), TimeSpan.Zero));
        var fields = Form(day);
        var first = await BookingAsync(await PostFormAsync(fields));
        var retry = await PostFormAsync(fields);
        Assert.Equal(HttpStatusCode.OK, retry.StatusCode);
        Assert.Equal(first.Id, (await BookingAsync(retry)).Id);
    }

    [Fact]
    public async Task GetQueryAndJsonBody_AreAccepted_DateComparedInLocalTimeZone()
    {
        var day = NextDay();
        // 23:30 UTC on the previous day is 09:30 local time in Vladivostok on the chosen day.
        var session = await SessionAsync(new DateTimeOffset(day.AddDays(-1).ToDateTime(new TimeOnly(23, 30)), TimeSpan.Zero), "Asia/Vladivostok");

        var query = string.Join("&", Form(day, "09:30").Select(x => $"{x.Key}={Uri.EscapeDataString(x.Value)}"));
        var get = await app.Browser().GetAsync($"{Url}?{query}");
        Assert.Equal(HttpStatusCode.Created, get.StatusCode);
        Assert.Equal(session.Id, (await BookingAsync(get)).GameSessionId);

        var json = await app.Browser().PostAsJsonAsync(Url, new
        {
            key = AppFactory.IntegrationSecret, game_date = day.ToString("dd.MM.yyyy"), game_time = "09:30", quantity = 2,
            name = "Борис", phone = "+7 900 000 00 42", team = "JSON-команда", submission_id = Guid.NewGuid().ToString()
        });
        Assert.Equal(HttpStatusCode.Created, json.StatusCode);
        Assert.Equal(2, (await BookingAsync(json)).Quantity);
    }

    [Fact]
    public async Task Rejections_HaveClearReasons()
    {
        var day = NextDay();
        await SessionAsync(new DateTimeOffset(day.ToDateTime(new TimeOnly(18, 0)), TimeSpan.Zero), title: "Ранняя");
        await SessionAsync(new DateTimeOffset(day.ToDateTime(new TimeOnly(21, 0)), TimeSpan.Zero), title: "Поздняя");
        var closedDay = NextDay();
        await SessionAsync(new DateTimeOffset(closedDay.ToDateTime(new TimeOnly(19, 0)), TimeSpan.Zero), open: false);

        async Task<(HttpStatusCode Code, string Body)> Send(Dictionary<string, string> f)
        {
            var r = await PostFormAsync(f);
            return (r.StatusCode, await r.Content.ReadAsStringAsync());
        }

        var wrongKey = Form(day, "18:00"); wrongKey["key"] = "wrong";
        Assert.Equal(HttpStatusCode.Unauthorized, (await Send(wrongKey)).Code);
        var noKey = Form(day, "18:00"); noKey.Remove("key");
        Assert.Equal(HttpStatusCode.Unauthorized, (await Send(noKey)).Code);

        var missing = Form(day, "18:00"); missing.Remove("phone"); missing.Remove("team");
        var (code, body) = await Send(missing);
        Assert.Equal(HttpStatusCode.BadRequest, code);
        Assert.Contains("phone", body); Assert.Contains("team", body);

        Assert.Equal(HttpStatusCode.BadRequest, (await Send(Form(day, "18:00", quantity: "Не выбрано"))).Code);
        Assert.Equal(HttpStatusCode.NotFound, (await Send(Form(NextDay()))).Code);                 // no game that day
        var ambiguous = Form(day, time: null); ambiguous["game_name"] = "Игра";
        Assert.Equal(HttpStatusCode.Conflict, (await Send(ambiguous)).Code);                       // two games, no hint
        var byTitle = Form(day, time: null); byTitle["game_name"] = "Поздняя";
        Assert.Equal(HttpStatusCode.Created, (await Send(byTitle)).Code);                          // title disambiguates
        Assert.Equal(HttpStatusCode.Conflict, (await Send(Form(closedDay))).Code);                 // sales closed
    }

    [Fact]
    public async Task MoreTicketsThanRemaining_IsStillAccepted_AsPendingRequest()
    {
        var day = NextDay();
        var session = await SessionAsync(new DateTimeOffset(day.ToDateTime(new TimeOnly(19, 0)), TimeSpan.Zero), capacity: 5);
        using (var scope = app.Services.CreateScope())
        {
            var db = scope.ServiceProvider.GetRequiredService<AppDbContext>();
            var admin = await app.CreateUserAsync("Admin");
            var paid = new Booking { GameSessionId = session.Id, TeamName = "Ранние", CustomerName = "К", Phone = "+79000000043",
                Quantity = 4, TicketPriceSnapshot = 700m, Status = BookingStatus.Confirmed };
            db.Bookings.Add(paid);
            db.Payments.Add(new Payment { Booking = paid, Amount = 2800m, ConfirmedByUserId = admin.Id });
            await db.SaveChangesAsync();
        }
        // Only 1 seat left: the customer already saw "sent" in FormDesigner, so the request must not be lost.
        var response = await PostFormAsync(Form(day, quantity: "3"));
        Assert.Equal(HttpStatusCode.Created, response.StatusCode);
        Assert.Equal(BookingStatus.Pending, (await BookingAsync(response)).Status);
        Assert.Equal(HttpStatusCode.Conflict, (await PostFormAsync(Form(day, quantity: "6"))).StatusCode);  // above capacity
    }
}
