using System.Net;
using System.Net.Http.Headers;
using System.Text.RegularExpressions;
using Microsoft.AspNetCore.Identity;
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

    /// <summary>Each test books with its own phone: the phone is the login of the customer's account.</summary>
    private static string NewPhone() => "8 (9" + Random.Shared.Next(10, 99) + ") " + Random.Shared.Next(100, 999) + "-" + Random.Shared.Next(10, 99) + "-" + Random.Shared.Next(10, 99);
    private static string Normalized(string phone) => ContactRules.NormalizePhone(phone)!;

    private static Dictionary<string, string> Fields(GameSession s, Guid? key = null, string quantity = "3", string telegram = "t.me/team_cat",
        bool consent = true, string? phone = null) => new()
    {
        ["Input.SessionId"] = s.Id.ToString(), ["Input.SubmissionKey"] = (key ?? Guid.NewGuid()).ToString(),
        ["Input.Quantity"] = quantity, ["Input.Name"] = "ТЕСТ", ["Input.Phone"] = phone ?? NewPhone(),
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
    public async Task Submit_CreatesAccountByPhone_SignsIn_AndShowsThePasswordOnce()
    {
        var s = await SessionAsync();
        var client = app.Browser();
        var key = Guid.NewGuid();
        var phone = NewPhone();

        var review = await PostAsync(client, s, "Review", Fields(s, key, phone: phone));
        Assert.Equal(HttpStatusCode.OK, review.StatusCode);
        var reviewHtml = WebUtility.HtmlDecode(await review.Content.ReadAsStringAsync());
        Assert.Contains("Пожалуйста, перепроверьте", reviewHtml);
        Assert.Contains(Normalized(phone), reviewHtml);                    // normalised before the customer confirms
        Assert.Contains("2 100 ₽", reviewHtml.Replace('\u00A0', ' '));
        Assert.Empty(await BookingsAsync(s.Id));                           // nothing saved until «К оплате»

        var submit = await PostAsync(client, s, "Submit", Fields(s, key, phone: phone));
        Assert.Equal(HttpStatusCode.Redirect, submit.StatusCode);
        Assert.StartsWith("/bookings/mine", submit.Headers.Location!.OriginalString);   // straight into the personal account
        var again = await PostAsync(client, s, "Submit", Fields(s, key, phone: phone)); // double click / resent form
        Assert.Equal(HttpStatusCode.Redirect, again.StatusCode);

        var booking = Assert.Single(await BookingsAsync(s.Id));
        Assert.Equal((3, Normalized(phone), "@team_cat", 700m), (booking.Quantity, booking.Phone, booking.Telegram, booking.TicketPriceSnapshot));
        Assert.NotNull(booking.PrivacyConsentAt);
        using (var scope = app.Services.CreateScope())
        {
            var account = await scope.ServiceProvider.GetRequiredService<UserManager<AppUser>>().FindByNameAsync(Normalized(phone));
            Assert.NotNull(account);
            Assert.Equal(account!.Id, booking.OwnerUserId);
        }

        var mine = WebUtility.HtmlDecode(await client.GetStringAsync("/bookings/mine"));
        var password = Regex.Match(mine, "<code>([a-z0-9]{10})</code>").Groups[1].Value;
        Assert.NotEmpty(password);                                           // shown once…
        Assert.Contains(Normalized(phone), mine);
        Assert.Contains(booking.TeamName, mine);
        Assert.Contains("загрузите чек", mine);
        Assert.DoesNotContain("<code>", WebUtility.HtmlDecode(await client.GetStringAsync("/bookings/mine")));  // …and only once

        // The phone and the generated password log in (typed in any format); a customer lands in the account.
        var other = app.Browser();
        var login = await AppFactory.PostFormAsync(other, "/account/login", new() { ["Input.Login"] = phone.Replace(" ", ""), ["Input.Password"] = password });
        Assert.Equal("/bookings/mine", login.Headers.Location?.OriginalString);

        // The customer sets an own password: 6+ characters with a digit is enough for customers.
        var changed = await AppFactory.PostFormAsync(other, "/account/change-password", new()
        { ["Input.CurrentPassword"] = password, ["Input.NewPassword"] = "kotik7", ["Input.ConfirmPassword"] = "kotik7" });
        Assert.Equal(HttpStatusCode.Redirect, changed.StatusCode);
        var relogin = await AppFactory.PostFormAsync(app.Browser(), "/account/login", new() { ["Input.Login"] = Normalized(phone), ["Input.Password"] = "kotik7" });
        Assert.Equal(HttpStatusCode.Redirect, relogin.StatusCode);
    }

    [Fact]
    public async Task KnownPhone_IsNotSignedIn_BookingGoesToThatAccount_AndPayScreenTakesTheReceipt()
    {
        var s = await SessionAsync();
        var phone = NewPhone();
        await PostAsync(app.Browser(), s, "Submit", Fields(s, phone: phone));       // creates the account

        var stranger = app.Browser();
        var submit = await PostAsync(stranger, s, "Submit", Fields(s, phone: phone));
        var payUrl = submit.Headers.Location!.OriginalString;
        Assert.StartsWith("/oplata/", payUrl);                                       // no sign-in from a typed phone
        Assert.Equal(HttpStatusCode.Redirect, (await stranger.GetAsync("/bookings/mine")).StatusCode);
        var bookings = await BookingsAsync(s.Id);
        Assert.True(bookings.Count == 2, "both bookings are kept");
        Assert.Single(bookings.Select(x => x.OwnerUserId).Distinct());

        var pay = await stranger.GetAsync(payUrl);
        Assert.True(pay.Headers.CacheControl?.NoStore);
        Assert.Contains("Заявка принята", WebUtility.HtmlDecode(await pay.Content.ReadAsStringAsync()));
        var upload = await UploadReceiptAsync(stranger, payUrl, payUrl + "?handler=Receipt", null, Png);
        Assert.Equal(HttpStatusCode.Redirect, upload.StatusCode);
        Assert.Contains("Чек отправлен", WebUtility.HtmlDecode(await stranger.GetStringAsync(payUrl)));
    }

    private static readonly byte[] Png = [0x89, 0x50, 0x4E, 0x47, 0x0D, 0x0A, 0x1A, 0x0A, 5, 5, 5, 5];

    private static async Task<HttpResponseMessage> UploadReceiptAsync(HttpClient client, string pageUrl, string postUrl, Guid? bookingId, byte[] bytes, string name = "check.png")
    {
        var form = new MultipartFormDataContent
        {
            { new StringContent(await AppFactory.AntiforgeryTokenAsync(client, pageUrl)), "__RequestVerificationToken" },
            { new ByteArrayContent(bytes) { Headers = { ContentType = new MediaTypeHeaderValue("application/octet-stream") } }, "receipt", name }
        };
        if (bookingId is not null) form.Add(new StringContent(bookingId.ToString()!), "id");
        return await client.PostAsync(postUrl, form);
    }

    [Fact]
    public async Task Receipt_UploadedInTheAccount_IsShownToStaffInPendingCard_AndNobodyElse()
    {
        var s = await SessionAsync();
        var client = app.Browser();
        await PostAsync(client, s, "Submit", Fields(s));
        var booking = Assert.Single(await BookingsAsync(s.Id));

        var rejected = await UploadReceiptAsync(client, "/bookings/mine", "/bookings/mine?handler=Receipt", booking.Id, "not a receipt"u8.ToArray(), "x.png");
        Assert.Equal(HttpStatusCode.Redirect, rejected.StatusCode);
        Assert.Null(Assert.Single(await BookingsAsync(s.Id)).ReceiptKey);           // only images and PDF
        var pdf = "%PDF-1.4 receipt"u8.ToArray();
        Assert.Equal(HttpStatusCode.Redirect, (await UploadReceiptAsync(client, "/bookings/mine", "/bookings/mine?handler=Receipt", booking.Id, pdf, "check.pdf")).StatusCode);
        Assert.Equal(HttpStatusCode.Redirect, (await UploadReceiptAsync(client, "/bookings/mine", "/bookings/mine?handler=Receipt", booking.Id, Png)).StatusCode);
        Assert.Single(Directory.GetFiles(Path.Combine(AppFactory.ImagesPath, "receipts"), $"{booking.Id:N}-*"));  // replaced, not piled up

        var admin = await app.SignedInAsync(await app.CreateUserAsync("Admin"));
        var pending = WebUtility.HtmlDecode(await admin.GetStringAsync("/bookings/pending?q=" + Uri.EscapeDataString(booking.TeamName)));
        Assert.Contains("Чек клиента", pending);
        Assert.Contains($"/receipts/{booking.Id}", pending);
        Assert.Equal(Png, await (await admin.GetAsync($"/receipts/{booking.Id}")).Content.ReadAsByteArrayAsync());
        Assert.Equal(Png, await (await client.GetAsync($"/receipts/{booking.Id}")).Content.ReadAsByteArrayAsync());   // the owner

        Assert.Equal(HttpStatusCode.Redirect, (await app.Browser().GetAsync($"/receipts/{booking.Id}")).StatusCode);  // anonymous → login
        var otherClient = await app.SignedInAsync(await app.CreateUserAsync("User"));
        Assert.Equal(HttpStatusCode.NotFound, (await otherClient.GetAsync($"/receipts/{booking.Id}")).StatusCode);
        var outsider = await app.SignedInAsync(await app.CreateUserAsync("Manager"));                         // not assigned to the game
        Assert.Equal(HttpStatusCode.NotFound, (await outsider.GetAsync($"/receipts/{booking.Id}")).StatusCode);
        Assert.Equal(HttpStatusCode.NotFound, (await UploadReceiptAsync(otherClient, "/bookings/mine", "/bookings/mine?handler=Receipt", booking.Id, Png)).StatusCode);
    }

    [Fact]
    public async Task Admin_ResetsACustomersPassword()
    {
        var s = await SessionAsync();
        var phone = NewPhone();
        await PostAsync(app.Browser(), s, "Submit", Fields(s, phone: phone));
        string userId;
        using (var scope = app.Services.CreateScope())
            userId = (await scope.ServiceProvider.GetRequiredService<UserManager<AppUser>>().FindByNameAsync(Normalized(phone)))!.Id;

        var admin = await app.SignedInAsync(await app.CreateUserAsync("Admin"));
        var reset = await AppFactory.PostFormAsync(admin, "/staff?handler=ResetPassword", new() { ["userId"] = userId });
        Assert.Equal(HttpStatusCode.Redirect, reset.StatusCode);
        var notice = WebUtility.HtmlDecode(await admin.GetStringAsync("/staff"));
        var password = Regex.Match(notice, Regex.Escape(Normalized(phone)) + ": ([a-z0-9]{10})").Groups[1].Value;
        Assert.NotEmpty(password);
        var login = await AppFactory.PostFormAsync(app.Browser(), "/account/login", new() { ["Input.Login"] = phone, ["Input.Password"] = password });
        Assert.Equal("/bookings/mine", login.Headers.Location?.OriginalString);
    }

    [Fact]
    public async Task StaleToken_ReturnsTheFormWithTypedValues_AndSavesNothing()
    {
        var s = await SessionAsync();
        var client = app.Browser();
        var fields = Fields(s);
        fields["__RequestVerificationToken"] = "stale-token-from-an-old-page";
        foreach (var handler in new[] { "Review", "Submit" })
        {
            var response = await client.PostAsync($"/?handler={handler}", new FormUrlEncodedContent(fields));
            Assert.Equal(HttpStatusCode.OK, response.StatusCode);              // not a bare 400
            var html = WebUtility.HtmlDecode(await response.Content.ReadAsStringAsync());
            Assert.Contains("Страница устарела", html);
            Assert.Contains(fields["Input.Team"], html);                       // what was typed is kept
            Assert.Matches("name=\"__RequestVerificationToken\"", html);      // with a fresh token
        }
        Assert.Empty(await BookingsAsync(s.Id));
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
