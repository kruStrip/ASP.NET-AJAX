using System.Security.Claims;
using Microsoft.AspNetCore.Identity;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using MuzLoto.Web.Data;
using MuzLoto.Web.Domain;
using MuzLoto.Web.Services;
using Xunit;

namespace MuzLoto.Tests;

// Runs alone, so it never creates roles while the app seeder does the same on a fresh database.
// (No app fixture: the unit tests in this class must also run without PostgreSQL.)
[CollectionDefinition(nameof(StaffManagementTests), DisableParallelization = true)]
[Collection(nameof(StaffManagementTests))]
public sealed class StaffManagementTests
{
    [Theory]
    [InlineData("Admin", true)]
    [InlineData("Manager", true)]
    [InlineData("User", true)]
    [InlineData("administrator", false)]
    [InlineData("", false)]
    public void OnlyKnownRolesAllowed(string role, bool allowed) =>
        Assert.Equal(allowed, StaffManagement.IsAllowedRole(role));

    [Fact]
    [Trait("Category", "Integration")]
    public async Task ManagerSeesOnlyAssignedSessions_RoleChangeRevokesAssignments_AndStamp()
    {
        if (Environment.GetEnvironmentVariable("MUZLOTO_PG_TEST") != "1")
            throw new InvalidOperationException("Integration tests require isolated PostgreSQL database.");
        var connection = Environment.GetEnvironmentVariable("ConnectionStrings__Default")
            ?? throw new InvalidOperationException("ConnectionStrings__Default missing.");
        var settings = new ConfigurationBuilder().AddInMemoryCollection(new Dictionary<string,string?>()).Build();
        var collection = new ServiceCollection();
        collection.AddLogging();
        collection.AddSingleton<IConfiguration>(settings);
        collection.AddDbContext<AppDbContext>(o => o.UseNpgsql(connection));
        collection.AddIdentityCore<AppUser>(options =>
        {
            options.User.RequireUniqueEmail = true;
            options.Password.RequiredLength = 12;
        }).AddRoles<IdentityRole>().AddEntityFrameworkStores<AppDbContext>();
        collection.AddSignalR();
        collection.AddSingleton<MuzLoto.Web.Hubs.HubConnectionTracker>();
        collection.AddScoped<MuzLoto.Web.Hubs.HubAccessRevoker>();
        collection.AddScoped<StaffManagement>();
        collection.AddScoped<StaffAccess>();
        using var provider = collection.BuildServiceProvider();
        using var scope = provider.CreateScope();
        var users = scope.ServiceProvider.GetRequiredService<UserManager<AppUser>>();
        var roles = scope.ServiceProvider.GetRequiredService<RoleManager<IdentityRole>>();
        var db = scope.ServiceProvider.GetRequiredService<AppDbContext>();
        var management = scope.ServiceProvider.GetRequiredService<StaffManagement>();
        var access = scope.ServiceProvider.GetRequiredService<StaffAccess>();

        foreach (var role in StaffManagement.Roles)
        {
            if (!await roles.RoleExistsAsync(role))
                Assert.True((await roles.CreateAsync(new IdentityRole(role))).Succeeded);
        }

        var email = "manager-ci-" + Guid.NewGuid().ToString("N") + "@example.invalid";
        var manager = await management.CreateAsync("Тестовый менеджер", email, "TemporaryPassword123!", "Manager");
        Assert.True(await users.CheckPasswordAsync(manager, "TemporaryPassword123!"));
        Assert.NotEqual("TemporaryPassword123!", manager.PasswordHash);
        var initialStamp = await users.GetSecurityStampAsync(manager);
        var duplicate = await Assert.ThrowsAsync<BusinessException>(() =>
            management.CreateAsync("Другой менеджер", email, "TemporaryPassword123!", "Manager"));
        Assert.Equal(409, duplicate.StatusCode);

        var a = new GameSession
        {
            Game = new Game { Title = "CI Manager A" }, StartsAt = DateTimeOffset.UtcNow.AddDays(5),
            TimeZoneId = "Etc/UTC", Venue = "CI", Capacity = 20, TicketPrice = 500
        };
        var b = new GameSession
        {
            Game = new Game { Title = "CI Manager B" }, StartsAt = DateTimeOffset.UtcNow.AddDays(6),
            TimeZoneId = "Etc/UTC", Venue = "CI", Capacity = 20, TicketPrice = 500
        };
        db.GameSessions.AddRange(a, b);
        await db.SaveChangesAsync();
        var principal = new ClaimsPrincipal(new ClaimsIdentity(new[]
        {
            new Claim(ClaimTypes.NameIdentifier, manager.Id), new Claim(ClaimTypes.Role, "Manager")
        }, "test"));
        Assert.False(await access.CanManageAsync(principal, a.Id));
        await management.AssignManagerAsync(a.Id, manager.Id);
        await management.AssignManagerAsync(a.Id, manager.Id);
        Assert.True(await access.CanManageAsync(principal, a.Id));
        Assert.False(await access.CanManageAsync(principal, b.Id));
        Assert.Equal(1, await db.EventStaffAssignments.CountAsync(x => x.UserId == manager.Id));

        // Non-Managers cannot be assigned.
        var ordinaryUser = await management.CreateAsync(
            "Обычный пользователь", "user-ci-" + Guid.NewGuid().ToString("N") + "@example.invalid",
            "TemporaryPassword123!", "User");
        var notManager = await Assert.ThrowsAsync<BusinessException>(() =>
            management.AssignManagerAsync(b.Id, ordinaryUser.Id));
        Assert.Equal(409, notManager.StatusCode);

        await management.RemoveManagerAsync(a.Id, manager.Id);
        Assert.False(await access.CanManageAsync(principal, a.Id));
        await management.AssignManagerAsync(a.Id, manager.Id);
        var selfChange = await Assert.ThrowsAsync<BusinessException>(() =>
            management.ChangeRoleAsync(manager.Id, "User", manager.Id));
        Assert.Equal(409, selfChange.StatusCode);
        await management.ChangeRoleAsync(manager.Id, "User", "admin-test-actor");
        Assert.Empty(await db.EventStaffAssignments.Where(x => x.UserId == manager.Id).ToListAsync());
        Assert.True(await users.IsInRoleAsync(manager, "User"));
        Assert.False(await users.IsInRoleAsync(manager, "Manager"));
        Assert.NotEqual(initialStamp, await users.GetSecurityStampAsync(manager));
    }
}
