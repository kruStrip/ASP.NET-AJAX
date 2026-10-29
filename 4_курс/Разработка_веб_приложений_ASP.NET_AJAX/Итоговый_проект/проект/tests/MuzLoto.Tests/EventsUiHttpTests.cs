using System.Net;
using System.Net.Http.Headers;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using MuzLoto.Web.Data;
using MuzLoto.Web.Domain;
using Xunit;

namespace MuzLoto.Tests;

/// <summary>Game card, several dates per game, covers, archive and search through the Razor pages.</summary>
[Trait("Category", "Integration")]
[Collection(AppCollection.Name)]
public sealed class EventsUiHttpTests(AppFactory app)
{
    private static readonly byte[] Png = [0x89, 0x50, 0x4E, 0x47, 0x0D, 0x0A, 0x1A, 0x0A, 0, 0, 0, 13, 1, 2, 3, 4];

    private static Dictionary<string, string> GameFields(string title) => new()
    {
        ["Input.Title"] = title,
        ["Input.Description1"] = "Первый абзац",
        ["Input.Description2"] = "<script>alert('XSS')</script>",
        ["Input.LocalStart"] = DateTime.Today.AddDays(9).ToString("dd.MM.yyyy 19:00"),
        ["Input.Venue"] = "Тестовый бар",
        ["Input.MapUrl"] = "https://2gis.ru/vladivostok",
        ["Input.DurationMinutes"] = "90",
        ["Input.AgeLimit"] = "16",
        ["Input.TicketPrice"] = "700",
        ["Input.Capacity"] = "30"
    };

    private static Guid IdFrom(HttpResponseMessage redirect) =>
        Guid.Parse(redirect.Headers.Location!.OriginalString.Split('/', StringSplitOptions.RemoveEmptyEntries).Last());

    private async Task<T> DbAsync<T>(Func<AppDbContext, Task<T>> query)
    {
        using var scope = app.Services.CreateScope();
        return await query(scope.ServiceProvider.GetRequiredService<AppDbContext>());
    }

    private static async Task<HttpResponseMessage> UploadCoverAsync(HttpClient admin, Guid id, byte[] bytes, string name)
    {
        var token = await AppFactory.AntiforgeryTokenAsync(admin, $"/events/{id}");
        var form = new MultipartFormDataContent
        {
            { new StringContent(token), "__RequestVerificationToken" },
            { new ByteArrayContent(bytes) { Headers = { ContentType = new MediaTypeHeaderValue("application/octet-stream") } }, "cover", name }
        };
        return await admin.PostAsync($"/events/{id}?handler=Cover", form);
    }

    [Fact]
    public async Task CreateGame_WithFullCard_RendersEncoded_AndAddsSecondDate()
    {
        var admin = await app.SignedInAsync(await app.CreateUserAsync("Admin"));
        var title = "Карточка " + Guid.NewGuid().ToString("N")[..6];
        var created = await AppFactory.PostFormAsync(admin, "/events/create", GameFields(title));
        Assert.Equal(HttpStatusCode.Redirect, created.StatusCode);
        var id = IdFrom(created);

        var html = await admin.GetStringAsync($"/events/{id}");
        var stored = await DbAsync(db => db.GameSessions.AsNoTracking().SingleAsync(x => x.Id == id));
        Assert.Equal("Asia/Vladivostok", stored.TimeZoneId);                   // no time zone field: Vladivostok by default
        Assert.Equal(new TimeSpan(9, 0, 0), stored.StartsAt.UtcDateTime.TimeOfDay);  // 19:00 in Vladivostok = 09:00 UTC
        Assert.Contains(DateTime.Today.AddDays(9).ToString("dd.MM.yyyy") + " 19:00", html);
        Assert.DoesNotContain("Asia/Vladivostok", html);
        Assert.DoesNotContain("<script>alert", html);                        // Razor encodes user text
        Assert.Contains("&lt;script&gt;", html);
        Assert.Contains("href=\"https://2gis.ru/vladivostok\"", html);
        Assert.Contains("16+", html);

        var gameId = await DbAsync(db => db.GameSessions.Where(x => x.Id == id).Select(x => x.GameId).SingleAsync());
        var fields = GameFields("игнорируется");
        fields["Input.LocalStart"] = DateTime.Today.AddDays(16).ToString("dd.MM.yyyy 19:00");
        var second = await AppFactory.PostFormAsync(admin, $"/events/create?gameId={gameId}", fields);
        Assert.Equal(HttpStatusCode.Redirect, second.StatusCode);
        var secondId = IdFrom(second);
        Assert.NotEqual(id, secondId);
        Assert.Equal(2, await DbAsync(db => db.GameSessions.CountAsync(x => x.GameId == gameId)));
        Assert.Equal(title, await DbAsync(db => db.Games.Where(x => x.Id == gameId).Select(x => x.Title).SingleAsync()));
        Assert.Contains($"/events/{secondId}", await admin.GetStringAsync($"/events/{id}"));    // "other dates"
    }

    [Theory]
    [InlineData("Input.Title", "", "Укажите название игры")]
    [InlineData("Input.MapUrl", "javascript:alert(1)", "https://")]
    [InlineData("Input.Capacity", "0", "Мест: от 1")]
    [InlineData("Input.LocalStart", "2026-10-14T19:00", "в формате 14.10.2026 19:00")]
    [InlineData("Input.LocalStart", "31.02.2027 19:00", "в формате 14.10.2026 19:00")]
    public async Task CreateGame_InvalidInput_ShowsServerValidation(string field, string value, string message)
    {
        var admin = await app.SignedInAsync(await app.CreateUserAsync("Admin"));
        var title = "Ошибка " + Guid.NewGuid().ToString("N")[..6];
        var fields = GameFields(title);
        fields[field] = value;
        var response = await AppFactory.PostFormAsync(admin, "/events/create", fields);
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        Assert.Contains(message, WebUtility.HtmlDecode(await response.Content.ReadAsStringAsync()));
        Assert.False(await DbAsync(db => db.Games.AnyAsync(x => x.Title == title)));
    }

    [Fact]
    public async Task Cover_UploadReplaceAndReject_StaysWithSessionWhenDateChanges()
    {
        var admin = await app.SignedInAsync(await app.CreateUserAsync("Admin"));
        var id = IdFrom(await AppFactory.PostFormAsync(admin, "/events/create", GameFields("Обложка " + Guid.NewGuid().ToString("N")[..6])));

        Assert.Equal(HttpStatusCode.Redirect, (await UploadCoverAsync(admin, id, Png, "cover.png")).StatusCode);
        var image = await DbAsync(db => db.GameImages.AsNoTracking().SingleAsync(x => x.GameSessionId == id));
        Assert.Equal(1, image.Version);
        var served = await admin.GetAsync($"/images/{image.Id}?v=1");
        Assert.Equal("image/png", served.Content.Headers.ContentType?.MediaType);
        Assert.Equal(Png, await served.Content.ReadAsByteArrayAsync());
        var firstFile = Path.Combine(AppFactory.ImagesPath, image.StorageKey);
        Assert.True(File.Exists(firstFile));

        await UploadCoverAsync(admin, id, Png, "replacement.png");
        var replaced = await DbAsync(db => db.GameImages.AsNoTracking().SingleAsync(x => x.GameSessionId == id));
        Assert.Equal(image.Id, replaced.Id);
        Assert.Equal(2, replaced.Version);
        Assert.False(File.Exists(firstFile));   // the old version is removed

        await UploadCoverAsync(admin, id, "not an image"u8.ToArray(), "evil.png");
        Assert.Equal(2, (await DbAsync(db => db.GameImages.AsNoTracking().SingleAsync(x => x.GameSessionId == id))).Version);

        // Changing the date keeps the session ID, so the cover stays attached.
        var edit = GameFields("Обложка изменена");
        edit["Input.LocalStart"] = DateTime.Today.AddDays(30).ToString("dd.MM.yyyy 20:00");
        edit["SalesOpen"] = "true";
        Assert.Equal(HttpStatusCode.Redirect, (await AppFactory.PostFormAsync(admin, $"/events/{id}/edit", edit)).StatusCode);
        Assert.Equal(replaced.Id, (await DbAsync(db => db.GameImages.AsNoTracking().SingleAsync(x => x.GameSessionId == id))).Id);
        Assert.Contains($"/images/{replaced.Id}?v=2", await admin.GetStringAsync($"/events/{id}"));

        // Posters are public: the booking form on "/" shows them to every customer.
        Assert.Equal(HttpStatusCode.OK, (await app.Browser().GetAsync($"/images/{replaced.Id}")).StatusCode);
    }

    [Fact]
    public async Task Archive_HidesFromOperationalList_SearchAndDeleteRules()
    {
        var admin = await app.SignedInAsync(await app.CreateUserAsync("Admin"));
        var title = "Архив " + Guid.NewGuid().ToString("N")[..6];
        var id = IdFrom(await AppFactory.PostFormAsync(admin, "/events/create", GameFields(title)));
        var bookingId = await DbAsync(async db =>
        {
            var b = new Booking { GameSessionId = id, TeamName = "К", CustomerName = "К", Phone = "+79000000009", Quantity = 1, TicketPriceSnapshot = 700 };
            db.Bookings.Add(b);
            await db.SaveChangesAsync();
            return b.Id;
        });

        Assert.Contains(title, WebUtility.HtmlDecode(await admin.GetStringAsync($"/events?q={Uri.EscapeDataString(title)}")));
        Assert.DoesNotContain(title, WebUtility.HtmlDecode(await admin.GetStringAsync("/events?q=нет-такой-игры")));

        await AppFactory.PostFormAsync(admin, $"/events/{id}?handler=Archive", new());
        Assert.Null(await DbAsync(db => db.GameSessions.Where(x => x.Id == id).Select(x => x.ArchivedAt).SingleAsync())); // pending blocks

        await DbAsync(async db => { await db.Bookings.Where(x => x.Id == bookingId).ExecuteUpdateAsync(u => u.SetProperty(x => x.Status, BookingStatus.Confirmed)); return 0; });
        await AppFactory.PostFormAsync(admin, $"/events/{id}?handler=Delete", new());
        Assert.True(await DbAsync(db => db.GameSessions.AnyAsync(x => x.Id == id)));  // has bookings: not deleted

        await AppFactory.PostFormAsync(admin, $"/events/{id}?handler=Archive", new());
        Assert.NotNull(await DbAsync(db => db.GameSessions.Where(x => x.Id == id).Select(x => x.ArchivedAt).SingleAsync()));
        Assert.DoesNotContain(title, WebUtility.HtmlDecode(await admin.GetStringAsync("/events")));
        Assert.Contains(title, WebUtility.HtmlDecode(await admin.GetStringAsync("/events?status=archived")));

        await AppFactory.PostFormAsync(admin, $"/events/{id}?handler=Unarchive", new());
        var back = await DbAsync(db => db.GameSessions.AsNoTracking().SingleAsync(x => x.Id == id));
        Assert.Null(back.ArchivedAt);
        Assert.False(back.SalesOpen);

        var empty = IdFrom(await AppFactory.PostFormAsync(admin, "/events/create", GameFields("Пустая " + Guid.NewGuid().ToString("N")[..6])));
        await AppFactory.PostFormAsync(admin, $"/events/{empty}?handler=Delete", new());
        Assert.False(await DbAsync(db => db.GameSessions.AnyAsync(x => x.Id == empty)));
    }

    [Fact]
    public async Task Details_ManagerOnlyForAssignedGames()
    {
        var admin = await app.SignedInAsync(await app.CreateUserAsync("Admin"));
        var id = IdFrom(await AppFactory.PostFormAsync(admin, "/events/create", GameFields("Доступ " + Guid.NewGuid().ToString("N")[..6])));
        var manager = await app.CreateUserAsync("Manager");
        var client = await app.SignedInAsync(manager);
        var denied = await client.GetAsync($"/events/{id}");
        Assert.Equal(HttpStatusCode.Forbidden, (await client.GetAsync(denied.Headers.Location)).StatusCode);

        await DbAsync(async db => { db.EventStaffAssignments.Add(new EventStaffAssignment { UserId = manager.Id, GameSessionId = id }); return await db.SaveChangesAsync(); });
        var allowed = await client.GetAsync($"/events/{id}");
        Assert.Equal(HttpStatusCode.OK, allowed.StatusCode);
        Assert.DoesNotContain("handler=Archive", await allowed.Content.ReadAsStringAsync()); // admin-only actions hidden
    }
}
