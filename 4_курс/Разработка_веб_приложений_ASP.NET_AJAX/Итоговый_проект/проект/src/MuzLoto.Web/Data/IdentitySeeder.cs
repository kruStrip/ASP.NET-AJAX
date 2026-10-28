using Microsoft.AspNetCore.Identity;
using MuzLoto.Web.Domain;

namespace MuzLoto.Web.Data;

public static class IdentitySeeder
{
    /// <summary>Публичный пароль локального админа из appsettings.Development.json. Вне Development запрещён.</summary>
    public const string DevelopmentAdminPassword = "Admin2026!";

    public static async Task SeedAsync(IServiceProvider services, IConfiguration config, IHostEnvironment env)
    {
        using var scope = services.CreateScope();
        var roleManager = scope.ServiceProvider.GetRequiredService<RoleManager<IdentityRole>>();
        var userManager = scope.ServiceProvider.GetRequiredService<UserManager<AppUser>>();
        foreach (var role in new[] { "Admin", "Manager", "User" })
            if (!await roleManager.RoleExistsAsync(role))
                Ensure(await roleManager.CreateAsync(new IdentityRole(role)));

        // No embedded credentials: bootstrap only when explicit secrets are configured.
        var email = config["BootstrapAdmin:Email"];
        var password = config["BootstrapAdmin:Password"];
        if (string.IsNullOrWhiteSpace(email) && string.IsNullOrWhiteSpace(password)) return;
        if (string.IsNullOrWhiteSpace(email) || string.IsNullOrWhiteSpace(password))
            throw new InvalidOperationException("Both BootstrapAdmin:Email and BootstrapAdmin:Password are required.");
        if (!env.IsDevelopment() && password == DevelopmentAdminPassword)
            throw new InvalidOperationException("The published development admin password must not be used outside Development.");
        var user = await userManager.FindByEmailAsync(email);
        if (user is null)
        {
            user = new AppUser { UserName = email, Email = email, EmailConfirmed = true, FullName = "Администратор" };
            Ensure(await userManager.CreateAsync(user, password));
        }
        if (!await userManager.IsInRoleAsync(user, "Admin"))
            Ensure(await userManager.AddToRoleAsync(user, "Admin"));
    }

    private static void Ensure(IdentityResult result)
    {
        if (!result.Succeeded)
            throw new InvalidOperationException("Identity bootstrap failed: " + string.Join("; ", result.Errors.Select(e => e.Description)));
    }
}
