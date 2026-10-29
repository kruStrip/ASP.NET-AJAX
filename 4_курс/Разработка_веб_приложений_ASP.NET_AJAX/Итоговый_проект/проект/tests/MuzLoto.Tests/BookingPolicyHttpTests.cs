using System.Net;
using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Text.RegularExpressions;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using MuzLoto.Web.Data;
using MuzLoto.Web.Domain;
using Xunit;

namespace MuzLoto.Tests;

/// <summary>Resource-based booking policies and CSRF over real HTTP.</summary>
[Trait("Category", "Integration")]
[Collection(AppCollection.Name)]
public sealed class BookingPolicyHttpTests(AppFactory app)
{
    private async Task<Booking> CreatePendingBookingAsync(string? ownerUserId = null)
    {
        using var scope = app.Services.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<AppDbContext>();
        var session = new GameSession
        {
            Game = new Game { Title = "Политики " + Guid.NewGuid().ToString("N")[..8] },
            StartsAt = DateTimeOffset.UtcNow.AddDays(5), TimeZoneId = "Asia/Vladivostok",
            Venue = "Тестовый зал", TicketPrice = 700m, Capacity = 10
        };
        var booking = new Booking
        {
            GameSession = session, OwnerUserId = ownerUserId, TeamName = "Команда",
            CustomerName = "Клиент", Phone = "+79000000001", Quantity = 2, TicketPriceSnapshot = 700m
        };
        db.Bookings.Add(booking);
        await db.SaveChangesAsync();
        return booking;
    }

    private async Task<Booking> ReloadAsync(Guid id)
    {
        using var scope = app.Services.CreateScope();
        return await scope.ServiceProvider.GetRequiredService<AppDbContext>().Bookings.AsNoTracking().SingleAsync(x => x.Id == id);
    }

    [Fact]
    public async Task Owner_SeesAndEditsOwnBooking_OtherUserGets403()
    {
        var owner = await app.CreateUserAsync("User");
        var booking = await CreatePendingBookingAsync(owner.Id);
        var ownerClient = await app.SignedInAsync(owner);

        var mine = await ownerClient.GetStringAsync("/bookings/mine");
        Assert.Contains($"/bookings/{booking.Id}/edit", mine);
        var edit = await AppFactory.PostFormAsync(ownerClient, $"/bookings/{booking.Id}/edit", new()
        {
            ["Input.TeamName"] = "Новое имя команды", ["Input.CustomerName"] = "Клиент",
            ["Input.Telegram"] = "https://t.me/renamed_team", ["Input.Quantity"] = "5"
        });
        Assert.Equal(HttpStatusCode.Redirect, edit.StatusCode);
        var updated = await ReloadAsync(booking.Id);
        Assert.Equal(5, updated.Quantity);
        Assert.Equal("@renamed_team", updated.Telegram);

        var stranger = await app.SignedInAsync(await app.CreateUserAsync("User"));
        var denied = await stranger.GetAsync($"/bookings/{booking.Id}/edit");
        Assert.Equal(HttpStatusCode.Redirect, denied.StatusCode);
        Assert.Equal(HttpStatusCode.Forbidden, (await stranger.GetAsync(denied.Headers.Location)).StatusCode);
        Assert.DoesNotContain(booking.Id.ToString(), await stranger.GetStringAsync("/bookings/mine"));
    }

    [Fact]
    public async Task Owner_CannotConfirmOwnPayment()
    {
        var owner = await app.CreateUserAsync("User");
        var booking = await CreatePendingBookingAsync(owner.Id);
        var client = await app.SignedInAsync(owner);
        var response = await client.GetAsync("/bookings/pending");
        Assert.Equal(HttpStatusCode.Redirect, response.StatusCode); // staff-only page
        Assert.Equal(BookingStatus.Pending, (await ReloadAsync(booking.Id)).Status);
    }

    [Fact]
    public async Task AjaxConfirm_RequiresCsrfHeader()
    {
        var booking = await CreatePendingBookingAsync();
        var admin = await app.SignedInAsync(await app.CreateUserAsync("Admin"));
        var page = await admin.GetStringAsync("/bookings/pending");
        var token = Regex.Match(page, "<meta name=\"csrf-token\" content=\"([^\"]+)\"").Groups[1].Value;
        Assert.False(string.IsNullOrEmpty(token));

        HttpRequestMessage Ajax(bool withHeader)
        {
            var request = new HttpRequestMessage(HttpMethod.Post, "/bookings/pending?handler=Confirm")
            {
                Content = new FormUrlEncodedContent(new Dictionary<string, string> { ["id"] = booking.Id.ToString() })
            };
            request.Headers.Add("X-Requested-With", "XMLHttpRequest");
            if (withHeader) request.Headers.Add("X-CSRF-TOKEN", token);
            return request;
        }

        Assert.Equal(HttpStatusCode.BadRequest, (await admin.SendAsync(Ajax(withHeader: false))).StatusCode);
        Assert.Equal(BookingStatus.Pending, (await ReloadAsync(booking.Id)).Status);
        Assert.Equal(HttpStatusCode.OK, (await admin.SendAsync(Ajax(withHeader: true))).StatusCode);
        Assert.Equal(BookingStatus.Confirmed, (await ReloadAsync(booking.Id)).Status);
    }

    [Fact]
    public async Task JwtApi_IsNotBlockedByAntiforgery()
    {
        var admin = await app.CreateUserAsync("Admin");
        var client = app.Browser();
        var login = await client.PostAsJsonAsync("/api/auth/login", new { email = admin.Email, password = "Secret123" });
        var token = (await login.Content.ReadFromJsonAsync<Dictionary<string, object>>())!["accessToken"].ToString();
        using var request = new HttpRequestMessage(HttpMethod.Delete, $"/api/v1/events/{Guid.NewGuid()}");
        request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", token);
        Assert.Equal(HttpStatusCode.NotFound, (await client.SendAsync(request)).StatusCode);
    }
}
