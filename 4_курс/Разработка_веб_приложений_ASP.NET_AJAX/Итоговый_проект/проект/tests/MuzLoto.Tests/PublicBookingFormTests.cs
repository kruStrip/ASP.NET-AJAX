using System.Net;
using System.Net.Http.Headers;
using System.Text.RegularExpressions;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using MuzLoto.Web.Data;
using MuzLoto.Web.Domain;
using MuzLoto.Web.Services;
using Xunit;

namespace MuzLoto.Tests;

/// <summary>The site's own booking form on "/" (replaces FormDesigner) and the payment screen after «К оплате».</summary>
[Trait("Category", "Integration")]
[Collection(AppCollection.Name)]
public sealed class PublicBookingFormTests(AppFactory app)
{
    private async Task<GameSession> SessionAsync(int capacity = 10, int confirmed = 0, bool salesOpen = true)
    {
        using var scope = app.Services.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<AppDbContext>();
        var s = new GameSession
        {
            Game = new Game { Title = "Квиз " + Guid.NewGuid().ToString("N")[..6], Description1 = "Белла снова не может выбрать", Description4 = "Команда от 2х чел" },
            StartsAt = DateTimeOffset.UtcNow.AddDays(30), TimeZoneId = ScheduleTime.DefaultTimeZone, Venue = "Киса Бар, Светланская 21",
            TicketPrice = 700m, Capacity = capacity, SalesOpen = salesOpen, DurationMinutes = 120, AgeLimit = 18
        };
        db.GameSessions.Add(s);
        if (confirmed > 0)
            db.Bookings.Add(new Booking { GameSession = s, TeamName = "Оплатили", CustomerName = "К", Phone = "+79000000000",
                Quantity = confirmed, TicketPriceSnapshot = 700m, Status = BookingStatus.Confirmed });
        await db.SaveChangesAsync();
        return s;
    }

    private static Dictionary<string, string> Fields(GameSession s, Guid? key = null, string quantity = "3", string telegram = "t.me/team_cat",
        bool consent = true) => new()
    {
        ["Input.SessionId"] = s.Id.ToString(), ["Input.SubmissionKey"] = (key ?? Guid.NewGuid()).ToString(),
        ["Input.Quantity"] = quantity, ["Input.Name"] = "ТЕСТ", ["Input.Phone"] = "8 (924) 240-27-20",
        ["Input.Telegram"] = telegram, ["Input.Team"] = "МЯУ " + Guid.NewGuid().ToString("N")[..4],
        ["Input.AcceptPrivacy"] = consent ? "true" : "false"
    };

    private static async Task<HttpResponseMessage> PostAsync(HttpClient client, GameSession s, string handler, Dictionary<string, string> fields)
    {
        // Any page's token fits (the form itself is hidden for a closed or sold-out date).
        fields["__RequestVerificationToken"] = await AppFactory.AntiforgeryTokenAsync(client, "/account/login");
        return await client.PostAsync($"/?handler={handler}", new FormUrlEncodedContent(fields));
    }

    private async Task<List<Booking>> BookingsAsync(Guid sessionId)
    {
        using var scope = app.Services.CreateScope();
        return await scope.ServiceProvider.GetRequiredService<AppDbContext>().Bookings.AsNoTracking()
            .Where(x => x.GameSessionId == sessionId && x.Status == BookingStatus.Pending).ToListAsync();
    }

    [Fact]
    public async Task Home_ShowsGameCard_AndOffersOnlyRemainingSeats()
    {
        var s = await SessionAsync(capacity: 5, confirmed: 2);
        var html = WebUtility.HtmlDecode(await app.Browser().GetStringAsync($"/?game={s.Id}"));

        Assert.Contains($"<option value=\"{s.Id}\" selected=\"selected\">", html);
        Assert.Contains("Белла снова не может выбрать", html);
        Assert.Contains("Киса Бар, Светланская 21", html);
        Assert.Contains("2 часа", html);
        Assert.Contains("Кол-во билетов доступных для покупки: <strong>3</strong>", html);
        var quantitySelect = Regex.Match(html, "<select[^>]*Input_Quantity.*?</select>", RegexOptions.Singleline).Value;
        Assert.Equal(["", "1", "2", "3"], Regex.Matches(quantitySelect, "<option value=\"(\\d*)\"").Select(m => m.Groups[1].Value));
    }

    [Fact]
    public async Task Review_ThenSubmit_CreatesOnePendingBooking_AndShowsPaymentScreen()
    {
        var s = await SessionAsync();
        var client = app.Browser();
        var key = Guid.NewGuid();

        var review = await PostAsync(client, s, "Review", Fields(s, key));
        Assert.Equal(HttpStatusCode.OK, review.StatusCode);
        var reviewHtml = WebUtility.HtmlDecode(await review.Content.ReadAsStringAsync());
        Assert.Contains("Пожалуйста, перепроверьте", reviewHtml);
        Assert.Contains("+79242402720", reviewHtml);                      // normalised before the customer confirms
        Assert.Contains("2 100 ₽", reviewHtml.Replace(' ', ' '));
        Assert.Empty(await BookingsAsync(s.Id));                           // nothing saved until «К оплате»

        var submit = await PostAsync(client, s, "Submit", Fields(s, key));
        Assert.Equal(HttpStatusCode.Redirect, submit.StatusCode);
        var payUrl = submit.Headers.Location!.OriginalString;
        Assert.StartsWith("/oplata/", payUrl);
        var again = await PostAsync(client, s, "Submit", Fields(s, key)); // double click / resent form
        Assert.Equal(HttpStatusCode.Redirect, again.StatusCode);

        var booking = Assert.Single(await BookingsAsync(s.Id));
        Assert.Equal((3, "+79242402720", "@team_cat", 700m), (booking.Quantity, booking.Phone, booking.Telegram, booking.TicketPriceSnapshot));
        Assert.NotNull(booking.PrivacyConsentAt);
        Assert.Null(booking.OwnerUserId);

        var pay = await app.Browser().GetAsync(payUrl);                    // the link works on its own, e.g. on another device
        Assert.Equal(HttpStatusCode.OK, pay.StatusCode);
        Assert.True(pay.Headers.CacheControl?.NoStore);
        var payHtml = WebUtility.HtmlDecode(await pay.Content.ReadAsStringAsync());
        Assert.Contains("Заявка принята", payHtml);
        Assert.Contains(booking.TeamName, payHtml);
    }

    [Fact]
    public async Task InvalidInput_IsRejectedOnTheServer()
    {
        var s = await SessionAsync(capacity: 4, confirmed: 2);
        var client = app.Browser();

        var noConsent = WebUtility.HtmlDecode(await (await PostAsync(client, s, "Submit", Fields(s, consent: false))).Content.ReadAsStringAsync());
        Assert.Contains("Нужно согласие на обработку персональных данных.", noConsent);
        var email = WebUtility.HtmlDecode(await (await PostAsync(client, s, "Review", Fields(s, telegram: "me@mail.ru", quantity: "1"))).Content.ReadAsStringAsync());
        Assert.Contains("Укажите ник в формате @username", email);
        var tooMany = WebUtility.HtmlDecode(await (await PostAsync(client, s, "Submit", Fields(s, quantity: "3"))).Content.ReadAsStringAsync());
        Assert.Contains("Можно взять от 1 до 2 билетов.", tooMany);
        Assert.Empty(await BookingsAsync(s.Id));
    }

    [Fact]
    public async Task ClosedSale_IsNotListed_AndCannotBeBooked()
    {
        var closed = await SessionAsync(salesOpen: false);
        var client = app.Browser();
        Assert.DoesNotContain(closed.Id.ToString(), await client.GetStringAsync("/"));
        var html = WebUtility.HtmlDecode(await (await PostAsync(client, closed, "Submit", Fields(closed))).Content.ReadAsStringAsync());
        Assert.Contains("Запись на эту игру закрыта", html);
        Assert.Empty(await BookingsAsync(closed.Id));
    }

    [Fact]
    public async Task PaymentPage_RejectsForgedTokens()
    {
        var client = app.Browser();
        Assert.Equal(HttpStatusCode.NotFound, (await client.GetAsync($"/oplata/{Guid.NewGuid():N}")).StatusCode);
        Assert.Equal(HttpStatusCode.NotFound, (await client.GetAsync("/oplata/CfDJ8-garbage")).StatusCode);
    }

    [Fact]
    public async Task PaymentSettings_AdminOnly_TextAndQrAppearOnPaymentScreen()
    {
        Assert.Equal(HttpStatusCode.Redirect, (await app.Browser().GetAsync("/payment-settings")).StatusCode);
        var manager = await app.SignedInAsync(await app.CreateUserAsync("Manager"));
        Assert.Equal(HttpStatusCode.Redirect, (await manager.GetAsync("/payment-settings")).StatusCode);

        var admin = await app.SignedInAsync(await app.CreateUserAsync("Admin"));
        var text = "Перевод по номеру +7 900 000-00-00\nПосле оплаты пришлите чек " + Guid.NewGuid().ToString("N")[..6];
        var saved = await AppFactory.PostFormAsync(admin, "/payment-settings?handler=Text", new() { ["Instructions"] = text });
        Assert.Equal(HttpStatusCode.Redirect, saved.StatusCode);

        byte[] png = [0x89, 0x50, 0x4E, 0x47, 0x0D, 0x0A, 0x1A, 0x0A, 7, 7, 7, 7];
        var form = new MultipartFormDataContent
        {
            { new StringContent(await AppFactory.AntiforgeryTokenAsync(admin, "/payment-settings")), "__RequestVerificationToken" },
            { new ByteArrayContent(png) { Headers = { ContentType = new MediaTypeHeaderValue("image/png") } }, "image", "qr.png" }
        };
        Assert.Equal(HttpStatusCode.Redirect, (await admin.PostAsync("/payment-settings?handler=Qr", form)).StatusCode);
        var qr = await app.Browser().GetAsync("/oplata/qr");
        Assert.Equal(png, await qr.Content.ReadAsByteArrayAsync());

        var s = await SessionAsync();
        var client = app.Browser();
        var submit = await PostAsync(client, s, "Submit", Fields(s));
        var payHtml = WebUtility.HtmlDecode(await client.GetStringAsync(submit.Headers.Location!.OriginalString));
        Assert.Contains(text, payHtml.Replace("\r\n", "\n"));
        Assert.Contains("src=\"/oplata/qr?v=", payHtml);
    }
}
