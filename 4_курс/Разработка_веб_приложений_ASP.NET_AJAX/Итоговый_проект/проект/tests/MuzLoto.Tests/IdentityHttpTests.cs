using System.IdentityModel.Tokens.Jwt;
using System.Net;
using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Security.Claims;
using System.Text.RegularExpressions;
using Microsoft.AspNetCore.Identity;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.Extensions.DependencyInjection;
using MuzLoto.Web.Domain;
using Xunit;

namespace MuzLoto.Tests;

/// <summary>
/// Boots the real application over HTTP. Runs only when MUZLOTO_PG_TEST=1 against an isolated
/// PostgreSQL database (ConnectionStrings__Default) with Jwt__Key set; startup applies migrations.
/// </summary>
public sealed class AppFactory : WebApplicationFactory<Program>
{
    public AppFactory()
    {
        if (Environment.GetEnvironmentVariable("MUZLOTO_PG_TEST") != "1")
            throw new InvalidOperationException("Set MUZLOTO_PG_TEST=1 and use an isolated test database.");
        // Uploaded covers go to a throwaway folder, never into the source tree.
        Environment.SetEnvironmentVariable("Storage__ImagesPath", ImagesPath);
        Environment.SetEnvironmentVariable("Integration__Secret", IntegrationSecret);
    }

    public const string IntegrationSecret = "test-integration-secret-0123456789";
    public static readonly string ImagesPath = Path.Combine(Path.GetTempPath(), "muzloto-test-images");

    public HttpClient Browser() => CreateClient(new WebApplicationFactoryClientOptions
    {
        AllowAutoRedirect = false, HandleCookies = true
    });

    public async Task<AppUser> CreateUserAsync(string role, string password = "Secret123")
    {
        using var scope = Services.CreateScope();
        var users = scope.ServiceProvider.GetRequiredService<UserManager<AppUser>>();
        var email = $"{role.ToLowerInvariant()}-{Guid.NewGuid():N}@example.invalid";
        var user = new AppUser { UserName = email, Email = email, FullName = "Тестовый " + role };
        Assert.True((await users.CreateAsync(user, password)).Succeeded);
        Assert.True((await users.AddToRoleAsync(user, role)).Succeeded);
        return user;
    }

    public static async Task<string> AntiforgeryTokenAsync(HttpClient client, string url)
    {
        var html = await client.GetStringAsync(url);
        var match = Regex.Match(html, "name=\"__RequestVerificationToken\"[^>]*value=\"([^\"]+)\"");
        Assert.True(match.Success, "Antiforgery token not rendered on " + url);
        return match.Groups[1].Value;
    }

    public static async Task<HttpResponseMessage> PostFormAsync(HttpClient client, string url,
        Dictionary<string, string> fields)
    {
        fields["__RequestVerificationToken"] = await AntiforgeryTokenAsync(client, url);
        return await client.PostAsync(url, new FormUrlEncodedContent(fields));
    }

    /// <summary>Signs in without a cookie container and returns the raw Cookie header (for the SignalR client).</summary>
    public async Task<string> CookieHeaderAsync(AppUser user, string password = "Secret123")
    {
        var client = CreateClient(new WebApplicationFactoryClientOptions { AllowAutoRedirect = false, HandleCookies = false });
        static IEnumerable<string> Cookies(HttpResponseMessage r) =>
            r.Headers.TryGetValues("Set-Cookie", out var values) ? values.Select(v => v.Split(';')[0]) : [];
        var page = await client.GetAsync("/account/login");
        var antiforgeryCookie = string.Join("; ", Cookies(page));
        var token = Regex.Match(await page.Content.ReadAsStringAsync(),
            "name=\"__RequestVerificationToken\"[^>]*value=\"([^\"]+)\"").Groups[1].Value;
        using var login = new HttpRequestMessage(HttpMethod.Post, "/account/login")
        {
            Content = new FormUrlEncodedContent(new Dictionary<string, string>
            {
                ["Input.Email"] = user.Email!, ["Input.Password"] = password, ["__RequestVerificationToken"] = token
            })
        };
        login.Headers.Add("Cookie", antiforgeryCookie);
        var response = await client.SendAsync(login);
        Assert.Equal(HttpStatusCode.Redirect, response.StatusCode);
        return string.Join("; ", Cookies(response).Where(c => c.StartsWith(".AspNetCore.Identity.Application=", StringComparison.Ordinal)));
    }

    public async Task<HttpClient> SignedInAsync(AppUser user, string password = "Secret123")
    {
        var client = Browser();
        var response = await PostFormAsync(client, "/account/login", new()
        {
            ["Input.Email"] = user.Email!, ["Input.Password"] = password
        });
        Assert.Equal(HttpStatusCode.Redirect, response.StatusCode);
        return client;
    }
}

/// <summary>One application instance for all HTTP tests: no parallel startups racing to migrate the database.</summary>
[CollectionDefinition(Name)]
public sealed class AppCollection : ICollectionFixture<AppFactory>
{
    public const string Name = "MuzLoto app";
}

[Trait("Category", "Integration")]
[Collection(AppCollection.Name)]
public sealed class IdentityHttpTests(AppFactory app)
{
    private sealed record TokenDto(string AccessToken, string TokenType, DateTimeOffset ExpiresAt);

    [Fact]
    public async Task AnonymousHome_ShowsLoginAndRegisterLinks()
    {
        var html = await app.Browser().GetStringAsync("/");
        Assert.Contains("href=\"/account/login\"", html);
        Assert.Contains("href=\"/account/register\"", html);
    }

    [Fact]
    public async Task Register_AssignsUserRole_SignsIn_AndRedirectsHome()
    {
        var client = app.Browser();
        var email = $"new-{Guid.NewGuid():N}@example.invalid";
        var response = await AppFactory.PostFormAsync(client, "/account/register", new()
        {
            ["Input.FullName"] = "Новый Игрок", ["Input.Email"] = email,
            ["Input.Password"] = "abc123", ["Input.ConfirmPassword"] = "abc123"
        });

        Assert.Equal(HttpStatusCode.Redirect, response.StatusCode);
        Assert.Equal("/", response.Headers.Location?.OriginalString);
        var home = WebUtility.HtmlDecode(await client.GetStringAsync("/"));
        Assert.Contains("Новый Игрок", home);
        Assert.Contains("Выйти", home);

        using var scope = app.Services.CreateScope();
        var users = scope.ServiceProvider.GetRequiredService<UserManager<AppUser>>();
        var user = await users.FindByEmailAsync(email);
        Assert.NotNull(user);
        Assert.Equal(new[] { "User" }, await users.GetRolesAsync(user));
    }

    [Fact]
    public async Task Register_PasswordWithoutDigit_ShowsErrorAndCreatesNothing()
    {
        var client = app.Browser();
        var email = $"nodigit-{Guid.NewGuid():N}@example.invalid";
        var response = await AppFactory.PostFormAsync(client, "/account/register", new()
        {
            ["Input.FullName"] = "Без Цифры", ["Input.Email"] = email,
            ["Input.Password"] = "abcdef", ["Input.ConfirmPassword"] = "abcdef"
        });

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        Assert.Contains("цифру", WebUtility.HtmlDecode(await response.Content.ReadAsStringAsync()));
        using var scope = app.Services.CreateScope();
        Assert.Null(await scope.ServiceProvider.GetRequiredService<UserManager<AppUser>>().FindByEmailAsync(email));
    }

    [Fact]
    public async Task Register_WithoutAntiforgeryToken_Returns400()
    {
        var response = await app.Browser().PostAsync("/account/register", new FormUrlEncodedContent(
            new Dictionary<string, string>
            {
                ["Input.FullName"] = "CSRF", ["Input.Email"] = $"csrf-{Guid.NewGuid():N}@example.invalid",
                ["Input.Password"] = "abc123", ["Input.ConfirmPassword"] = "abc123"
            }));
        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
    }

    [Fact]
    public async Task AnonymousStaffPage_RedirectsToLogin()
    {
        var response = await app.Browser().GetAsync("/dashboard");
        Assert.Equal(HttpStatusCode.Redirect, response.StatusCode);
        Assert.StartsWith("/account/login", response.Headers.Location?.PathAndQuery);
    }

    [Fact]
    public async Task UserOpeningAdminPage_EndsWith403()
    {
        var client = await app.SignedInAsync(await app.CreateUserAsync("User"));
        var response = await client.GetAsync("/staff");
        Assert.Equal(HttpStatusCode.Redirect, response.StatusCode);
        var denied = await client.GetAsync(response.Headers.Location);
        Assert.Equal(HttpStatusCode.Forbidden, denied.StatusCode);
    }

    [Fact]
    public async Task ApiLogin_ForUser_IssuesShortLivedJwtWithIdEmailAndRole()
    {
        var user = await app.CreateUserAsync("User");
        var response = await app.Browser().PostAsJsonAsync("/api/auth/login",
            new { email = user.Email, password = "Secret123" });

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        var dto = (await response.Content.ReadFromJsonAsync<TokenDto>())!;
        var jwt = new JwtSecurityTokenHandler().ReadJwtToken(dto.AccessToken);
        Assert.Equal(user.Id, jwt.Claims.Single(x => x.Type == ClaimTypes.NameIdentifier).Value);
        Assert.Equal(user.Email, jwt.Claims.Single(x => x.Type == ClaimTypes.Email).Value);
        Assert.Equal("User", jwt.Claims.Single(x => x.Type == ClaimTypes.Role).Value);
        Assert.True(jwt.ValidTo - jwt.ValidFrom <= TimeSpan.FromHours(1));
    }

    [Fact]
    public async Task LegacyTokenRoute_StillWorks()
    {
        var user = await app.CreateUserAsync("Manager");
        var response = await app.Browser().PostAsJsonAsync("/api/auth/token",
            new { email = user.Email, password = "Secret123" });
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
    }

    [Fact]
    public async Task Api_WithoutToken401_UserTokenOnStaffEndpoint403()
    {
        var client = app.Browser();
        Assert.Equal(HttpStatusCode.Unauthorized, (await client.GetAsync("/api/v1/events")).StatusCode);

        var user = await app.CreateUserAsync("User");
        var login = await client.PostAsJsonAsync("/api/auth/login", new { email = user.Email, password = "Secret123" });
        var token = (await login.Content.ReadFromJsonAsync<TokenDto>())!.AccessToken;
        using var request = new HttpRequestMessage(HttpMethod.Get, "/api/v1/events");
        request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", token);
        Assert.Equal(HttpStatusCode.Forbidden, (await client.SendAsync(request)).StatusCode);
    }

    [Fact]
    public async Task FiveWrongPasswords_LockTheAccount()
    {
        var user = await app.CreateUserAsync("User");
        var client = app.Browser();
        for (var i = 0; i < 5; i++)
            await client.PostAsJsonAsync("/api/auth/login", new { email = user.Email, password = "wrong-0" });

        var correct = await client.PostAsJsonAsync("/api/auth/login", new { email = user.Email, password = "Secret123" });
        Assert.Equal(HttpStatusCode.Unauthorized, correct.StatusCode);
        using var scope = app.Services.CreateScope();
        var users = scope.ServiceProvider.GetRequiredService<UserManager<AppUser>>();
        var end = await users.GetLockoutEndDateAsync((await users.FindByIdAsync(user.Id))!);
        Assert.True(end > DateTimeOffset.UtcNow.AddMinutes(14));
    }
}
