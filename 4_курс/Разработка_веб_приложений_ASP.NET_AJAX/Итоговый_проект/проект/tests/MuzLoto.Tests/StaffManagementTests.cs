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

    private static ServiceProvider BuildProvider(string connection)
    {
        var settings = new ConfigurationBuilder().AddInMemoryCollection(new Dictionary<string, string?>()).Build();
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
        collection.AddScoped<SessionCrud>();
        return collection.BuildServiceProvider();
    }

    private static string TestConnection() =>
        Environment.GetEnvironmentVariable("MUZLOTO_PG_TEST") == "1"
            ? Environment.GetEnvironmentVariable("ConnectionStrings__Default")
                ?? throw new InvalidOperationException("ConnectionStrings__Default missing.")
            : throw new InvalidOperationException("Integration tests require isolated PostgreSQL database.");

    [Fact]
    [Trait("Category", "Integration")]
    public async Task ManagerSeesOnlyAssignedSessions_RoleChangeRevokesAssignments_AndStamp()
    {
        var connection = TestConnection();
        using var provider = BuildProvider(connection);
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
        Assert.Equal(1, await db.EventStaffAssignments.CountAsync(
            x => x.UserId == manager.Id && x.GameSessionId == a.Id));
        Assert.False(await db.EventStaffAssignments.AnyAsync(
            x => x.UserId == manager.Id && x.GameSessionId == b.Id));

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

    [Fact]
    [Trait("Category", "Integration")]
    public async Task NewSessionGetsFirstManagerByDefault_AndAssignmentsStayCustomisable()
    {
        using var provider = BuildProvider(TestConnection());
        using var scope = provider.CreateScope();
        var users = scope.ServiceProvider.GetRequiredService<UserManager<AppUser>>();
        var roles = scope.ServiceProvider.GetRequiredService<RoleManager<IdentityRole>>();
        var db = scope.ServiceProvider.GetRequiredService<AppDbContext>();
        var management = scope.ServiceProvider.GetRequiredService<StaffManagement>();
        var sessions = scope.ServiceProvider.GetRequiredService<SessionCrud>();

        foreach (var role in StaffManagement.Roles)
            if (!await roles.RoleExistsAsync(role))
                Assert.True((await roles.CreateAsync(new IdentityRole(role))).Succeeded);

        var defaultManager = await management.FindDefaultManagerAsync()
            ?? await management.CreateAsync("Первый менеджер", "default-manager-ci-" + Guid.NewGuid().ToString("N") + "@example.invalid",
                "TemporaryPassword123!", "Manager");
        Assert.True(await users.IsInRoleAsync(defaultManager, "Manager"));

        var game = new Game { Title = "CI Default Manager " + Guid.NewGuid().ToString("N") };
        db.Games.Add(game);
        await db.SaveChangesAsync();
        var created = await sessions.CreateAsync(new SessionWrite(
            game.Id, DateTimeOffset.UtcNow.AddDays(5), "Etc/UTC", "CI", 20, 500m, 120, 12, true), default);
        var assigned = await db.EventStaffAssignments.AsNoTracking()
            .SingleAsync(x => x.GameSessionId == created.Id);
        Assert.Equal(defaultManager.Id, assigned.UserId);

        // Reassigning stays explicit: the default manager can be removed, another one gets the game.
        var other = await management.CreateAsync("Другой менеджер", "other-manager-ci-" + Guid.NewGuid().ToString("N") + "@example.invalid",
            "TemporaryPassword123!", "Manager");
        await management.RemoveManagerAsync(created.Id, defaultManager.Id);
        await management.AssignManagerAsync(created.Id, other.Id);
        await management.AssignDefaultManagerAsync(created.Id);
        var ids = await db.EventStaffAssignments.AsNoTracking()
            .Where(x => x.GameSessionId == created.Id).Select(x => x.UserId).ToListAsync();
        Assert.Equal([other.Id], ids);
    }

    [Fact]
    [Trait("Category", "Integration")]
    public async Task EndSessions_InvalidatesTheStamp_DeleteKeepsBookingHistoryWithoutOwner()
    {
        using var provider = BuildProvider(TestConnection());
        using var scope = provider.CreateScope();
        var users = scope.ServiceProvider.GetRequiredService<UserManager<AppUser>>();
        var roles = scope.ServiceProvider.GetRequiredService<RoleManager<IdentityRole>>();
        var db = scope.ServiceProvider.GetRequiredService<AppDbContext>();
        var management = scope.ServiceProvider.GetRequiredService<StaffManagement>();

        foreach (var role in StaffManagement.Roles)
            if (!await roles.RoleExistsAsync(role))
                Assert.True((await roles.CreateAsync(new IdentityRole(role))).Succeeded);

        var customer = await management.CreateAsync("Клиент", "client-ci-" + Guid.NewGuid().ToString("N") + "@example.invalid",
            "TemporaryPassword123!", "User");
        var session = new GameSession
        {
            Game = new Game { Title = "CI Delete Account" }, StartsAt = DateTimeOffset.UtcNow.AddDays(7),
            TimeZoneId = "Etc/UTC", Venue = "CI", Capacity = 10, TicketPrice = 500
        };
        var booking = new Booking
        {
            GameSession = session, OwnerUserId = customer.Id, TeamName = "КИ", CustomerName = "Клиент",
            Phone = "+79140000001", Quantity = 2, TicketPriceSnapshot = 500m, Status = BookingStatus.Confirmed
        };
        db.Bookings.Add(booking);
        await db.SaveChangesAsync();

        // «Завершить сессии»: the account stays, but every previously issued cookie/JWT stops working.
        var stamp = await users.GetSecurityStampAsync(customer);
        await management.EndSessionsAsync(customer.Id);
        Assert.NotEqual(stamp, await users.GetSecurityStampAsync(customer));
        Assert.NotNull(await users.FindByIdAsync(customer.Id));

        // Own account cannot be deleted; the customer's can, and the paid booking stays in the history.
        var selfDelete = await Assert.ThrowsAsync<BusinessException>(() =>
            management.DeleteAsync(customer.Id, customer.Id));
        Assert.Equal(409, selfDelete.StatusCode);
        await management.DeleteAsync(customer.Id, "admin-test-actor");
        Assert.Null(await users.FindByIdAsync(customer.Id));
        var kept = await db.Bookings.AsNoTracking().SingleAsync(x => x.Id == booking.Id);
        Assert.Null(kept.OwnerUserId);
        Assert.Equal(BookingStatus.Confirmed, kept.Status);
    }
}
