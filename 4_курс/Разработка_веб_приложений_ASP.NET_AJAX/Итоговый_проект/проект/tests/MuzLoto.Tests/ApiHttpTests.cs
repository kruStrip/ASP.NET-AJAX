using System.Net;
using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Text.Json;
using Microsoft.Extensions.DependencyInjection;
using MuzLoto.Web.Data;
using MuzLoto.Web.Domain;
using Xunit;

namespace MuzLoto.Tests;

/// <summary>REST API v1/v2 over HTTP with real JWTs: CRUD, status codes, ownership and roles.</summary>
[Trait("Category", "Integration")]
[Collection(AppCollection.Name)]
public sealed class ApiHttpTests(AppFactory app)
{
    private async Task<(AppUser User, HttpClient Client)> ApiClientAsync(string role)
    {
        var user = await app.CreateUserAsync(role);
        var client = app.Browser();
        var login = await client.PostAsJsonAsync("/api/auth/login", new { email = user.Email, password = "Secret123" });
        var token = (await login.Content.ReadFromJsonAsync<JsonElement>()).GetProperty("accessToken").GetString();
        client.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", token);
        return (user, client);
    }

    private async Task<(Guid GameId, Guid SessionId)> CreateSessionAsync(string? managerId = null)
    {
        using var scope = app.Services.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<AppDbContext>();
        var session = new GameSession
        {
            Game = new Game { Title = "API " + Guid.NewGuid().ToString("N")[..8] },
            StartsAt = DateTimeOffset.UtcNow.AddDays(4), TimeZoneId = "Etc/UTC",
            Venue = "Зал", TicketPrice = 700m, Capacity = 10
        };
        db.GameSessions.Add(session);
        if (managerId is not null) db.EventStaffAssignments.Add(new EventStaffAssignment { UserId = managerId, GameSession = session });
        await db.SaveChangesAsync();
        return (session.GameId, session.Id);
    }

    private static object NewBooking(Guid eventId, int quantity = 2) => new
    {
        eventId, teamName = "Команда API", customerName = "Клиент", phone = "8 900 000 00 03",
        telegram = "@api_player", quantity
    };

    [Fact]
    public async Task Events_FullCrud_WithStatusCodes_AndV2Contract()
    {
        var (_, admin) = await ApiClientAsync("Admin");
        var (gameId, _) = await CreateSessionAsync();

        var create = await admin.PostAsJsonAsync("/api/v2/events", new
        {
            gameId, startsAt = DateTimeOffset.UtcNow.AddDays(10),
            location = new { name = "Новый зал", timeZone = "Europe/Moscow" }, seats = new { total = 30 },
            ticketPrice = 800, durationMinutes = 150, ageLimit = 18, salesOpen = true,
            mapUrl = "https://2gis.ru/vladivostok"
        });
        Assert.Equal(HttpStatusCode.Created, create.StatusCode);
        Assert.NotNull(create.Headers.Location);
        var created = await create.Content.ReadFromJsonAsync<JsonElement>();
        Assert.Equal(30, created.GetProperty("remainingTickets").GetInt32());
        Assert.Equal("https://2gis.ru/vladivostok", created.GetProperty("mapUrl").GetString());
        var id = created.GetProperty("id").GetGuid();

        Assert.Equal($"/api/v2/events/{id}", create.Headers.Location!.AbsolutePath);
        var v1 = await admin.GetFromJsonAsync<JsonElement>($"/api/v1/events/{id}");
        Assert.Equal(30, v1.GetProperty("capacity").GetInt32());   // v1: flat fields
        Assert.False(v1.TryGetProperty("mapUrl", out _));          // v1 contract has no mapUrl

        var put = await admin.PutAsJsonAsync($"/api/v1/events/{id}", new
        {
            gameId, startsAt = DateTimeOffset.UtcNow.AddDays(11), timeZoneId = "Europe/Moscow",
            venue = "Новый зал", capacity = 40, ticketPrice = 800, durationMinutes = 150, ageLimit = 18, salesOpen = true
        });
        Assert.Equal(HttpStatusCode.OK, put.StatusCode);
        var v2 = await (await admin.GetAsync($"/api/v2/events/{id}")).Content.ReadFromJsonAsync<JsonElement>();
        Assert.Equal(40, v2.GetProperty("remainingTickets").GetInt32());
        Assert.Equal("https://2gis.ru/vladivostok", v2.GetProperty("mapUrl").GetString()); // v1 PUT keeps it

        var invalid = await admin.PostAsJsonAsync("/api/v1/events", new { gameId, capacity = 0 });
        Assert.Equal(HttpStatusCode.BadRequest, invalid.StatusCode);
        Assert.Equal("application/problem+json", invalid.Content.Headers.ContentType?.MediaType);

        var unsafeLink = await admin.PutAsJsonAsync($"/api/v2/events/{id}", new
        {
            gameId, startsAt = DateTimeOffset.UtcNow.AddDays(11), location = new { name = "Зал", timeZone = "Europe/Moscow" },
            seats = new { total = 40 }, ticketPrice = 800, mapUrl = "javascript:alert(1)"
        });
        Assert.Equal(HttpStatusCode.BadRequest, unsafeLink.StatusCode);

        Assert.Equal(HttpStatusCode.NoContent, (await admin.DeleteAsync($"/api/v1/events/{id}")).StatusCode);
        Assert.Equal(HttpStatusCode.NotFound, (await admin.GetAsync($"/api/v1/events/{id}")).StatusCode);
    }

    [Fact]
    public async Task Bookings_UserOwnsWhatTheyCreate_OthersGet403()
    {
        var (_, sessionId) = await CreateSessionAsync();
        var (owner, ownerApi) = await ApiClientAsync("User");
        var (stranger, strangerApi) = await ApiClientAsync("User");

        Assert.Equal(HttpStatusCode.Unauthorized,
            (await app.Browser().PostAsJsonAsync("/api/v1/bookings", NewBooking(sessionId))).StatusCode);

        // An ownerUserId in the body is not part of the contract and cannot hijack ownership.
        var create = await ownerApi.PostAsJsonAsync("/api/v1/bookings", new
        {
            eventId = sessionId, teamName = "Команда API", customerName = "Клиент", phone = "8 900 000 00 03",
            quantity = 2, ownerUserId = stranger.Id
        });
        Assert.Equal(HttpStatusCode.Created, create.StatusCode);
        Assert.NotNull(create.Headers.Location);
        var booking = await create.Content.ReadFromJsonAsync<JsonElement>();
        var id = booking.GetProperty("id").GetGuid();
        Assert.Equal("pending", booking.GetProperty("status").GetString());
        Assert.Equal("+79000000003", booking.GetProperty("phone").GetString());
        Assert.True(booking.GetProperty("isOwn").GetBoolean());
        using (var scope = app.Services.CreateScope())
            Assert.Equal(owner.Id, (await scope.ServiceProvider.GetRequiredService<AppDbContext>().Bookings.FindAsync(id))!.OwnerUserId);

        var mine = await ownerApi.GetFromJsonAsync<JsonElement[]>("/api/v1/bookings");
        Assert.Contains(mine!, x => x.GetProperty("id").GetGuid() == id);
        var theirs = await strangerApi.GetFromJsonAsync<JsonElement[]>("/api/v1/bookings");
        Assert.DoesNotContain(theirs!, x => x.GetProperty("id").GetGuid() == id);

        Assert.Equal(HttpStatusCode.Forbidden, (await strangerApi.GetAsync($"/api/v1/bookings/{id}")).StatusCode);
        Assert.Equal(HttpStatusCode.Forbidden, (await strangerApi.PutAsJsonAsync($"/api/v1/bookings/{id}",
            new { teamName = "Захват", customerName = "Чужой", quantity = 1 })).StatusCode);

        var update = await ownerApi.PutAsJsonAsync($"/api/v1/bookings/{id}",
            new { teamName = "Переименована", customerName = "Клиент", quantity = 3 });
        Assert.Equal(HttpStatusCode.OK, update.StatusCode);
        Assert.Equal(3, (await update.Content.ReadFromJsonAsync<JsonElement>()).GetProperty("quantity").GetInt32());

        Assert.Equal(HttpStatusCode.NotFound, (await ownerApi.GetAsync($"/api/v1/bookings/{Guid.NewGuid()}")).StatusCode);
    }

    [Fact]
    public async Task Bookings_IdempotencyKey_ValidationAndCapacity()
    {
        var (_, sessionId) = await CreateSessionAsync();
        var (_, api) = await ApiClientAsync("User");
        var key = Guid.NewGuid().ToString();

        HttpRequestMessage Post(object body)
        {
            var request = new HttpRequestMessage(HttpMethod.Post, "/api/v1/bookings") { Content = JsonContent.Create(body) };
            request.Headers.Add("Idempotency-Key", key);
            return request;
        }
        var first = await api.SendAsync(Post(NewBooking(sessionId)));
        var repeat = await api.SendAsync(Post(NewBooking(sessionId)));
        Assert.Equal(HttpStatusCode.Created, first.StatusCode);
        Assert.Equal(HttpStatusCode.OK, repeat.StatusCode);
        Assert.Equal((await first.Content.ReadFromJsonAsync<JsonElement>()).GetProperty("id").GetGuid(),
            (await repeat.Content.ReadFromJsonAsync<JsonElement>()).GetProperty("id").GetGuid());

        var badPhone = await api.PostAsJsonAsync("/api/v1/bookings", new { eventId = sessionId, teamName = "К", customerName = "К", phone = "123", quantity = 1 });
        Assert.Equal(HttpStatusCode.BadRequest, badPhone.StatusCode);
        var tooMany = await api.PostAsJsonAsync("/api/v1/bookings", NewBooking(sessionId, quantity: 11));
        Assert.Equal(HttpStatusCode.Conflict, tooMany.StatusCode);
        var noEvent = await api.PostAsJsonAsync("/api/v1/bookings", NewBooking(Guid.NewGuid()));
        Assert.Equal(HttpStatusCode.NotFound, noEvent.StatusCode);
    }

    [Fact]
    public async Task Manager_SeesBookingsOfAssignedGamesOnly()
    {
        var (manager, managerApi) = await ApiClientAsync("Manager");
        var (_, assigned) = await CreateSessionAsync(manager.Id);
        var (_, foreign) = await CreateSessionAsync();
        var (_, userApi) = await ApiClientAsync("User");
        var inAssigned = (await (await userApi.PostAsJsonAsync("/api/v1/bookings", NewBooking(assigned)))
            .Content.ReadFromJsonAsync<JsonElement>()).GetProperty("id").GetGuid();
        var inForeign = (await (await userApi.PostAsJsonAsync("/api/v1/bookings", NewBooking(foreign)))
            .Content.ReadFromJsonAsync<JsonElement>()).GetProperty("id").GetGuid();

        Assert.Equal(HttpStatusCode.OK, (await managerApi.GetAsync($"/api/v1/bookings/{inAssigned}")).StatusCode);
        Assert.Equal(HttpStatusCode.Forbidden, (await managerApi.GetAsync($"/api/v1/bookings/{inForeign}")).StatusCode);
        var list = await managerApi.GetFromJsonAsync<JsonElement[]>("/api/v1/bookings?pageSize=100");
        Assert.Contains(list!, x => x.GetProperty("id").GetGuid() == inAssigned);
        Assert.DoesNotContain(list!, x => x.GetProperty("id").GetGuid() == inForeign);
    }

    [Fact]
    public async Task ReportsSummary_StaffOnly_CountsOnlyConfirmedSales()
    {
        var (_, userApi) = await ApiClientAsync("User");
        Assert.Equal(HttpStatusCode.Forbidden, (await userApi.GetAsync("/api/v1/reports/summary")).StatusCode);

        var (manager, managerApi) = await ApiClientAsync("Manager");
        var (_, sessionId) = await CreateSessionAsync(manager.Id);
        var empty = await managerApi.GetFromJsonAsync<JsonElement>("/api/v1/reports/summary");
        Assert.Equal(0, empty.GetProperty("ticketsSold").GetInt32());

        var bookingId = (await (await userApi.PostAsJsonAsync("/api/v1/bookings", NewBooking(sessionId, quantity: 4)))
            .Content.ReadFromJsonAsync<JsonElement>()).GetProperty("id").GetGuid();
        var pendingOnly = await managerApi.GetFromJsonAsync<JsonElement>("/api/v1/reports/summary");
        Assert.Equal(0, pendingOnly.GetProperty("ticketsSold").GetInt32());     // a request is not a sale
        Assert.Equal(1, pendingOnly.GetProperty("pendingRequests").GetInt32());

        using (var scope = app.Services.CreateScope())
            await scope.ServiceProvider.GetRequiredService<MuzLoto.Web.Services.BookingWorkflow>().ConfirmAsync(bookingId, manager.Id);
        var sold = await managerApi.GetFromJsonAsync<JsonElement>("/api/v1/reports/summary");
        Assert.Equal(4, sold.GetProperty("ticketsSold").GetInt32());
        Assert.Equal(1, sold.GetProperty("confirmedOrders").GetInt32());
        Assert.Equal(2800m, sold.GetProperty("confirmedRevenue").GetDecimal());
        Assert.Equal(2800m, sold.GetProperty("averageCheck").GetDecimal());
        Assert.Equal(4m, sold.GetProperty("averageTicketsPerOrder").GetDecimal());

        var (_, adminApi) = await ApiClientAsync("Admin");
        Assert.Equal(HttpStatusCode.OK, (await adminApi.GetAsync("/api/v1/reports/summary")).StatusCode);
    }

    [Fact]
    public async Task Swagger_PublishesBothVersions()
    {
        var client = app.Browser();
        var v1 = await client.GetStringAsync("/swagger/v1/swagger.json");
        var v2 = await client.GetStringAsync("/swagger/v2/swagger.json");
        Assert.Contains("/api/v1/bookings", v1);
        Assert.Contains("/api/v1/reports/summary", v1);
        Assert.Contains("/api/auth/login", v1);
        Assert.Contains("/api/v2/events", v2);
        Assert.DoesNotContain("/api/v1/events", v2);
        Assert.Contains("remainingTickets", v2);
        Assert.Contains("Bearer", v1);
    }
}
