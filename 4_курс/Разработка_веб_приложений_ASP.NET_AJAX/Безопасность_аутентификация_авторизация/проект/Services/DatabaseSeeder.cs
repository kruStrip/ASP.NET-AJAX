using Microsoft.AspNetCore.Identity;
using SecureTodo.Models;

namespace SecureTodo.Services;

public static class DatabaseSeeder
{
    private static readonly string[] Roles = ["Admin", "Author", "User"];

    public static async Task SeedAsync(IServiceProvider services)
    {
        using var scope = services.CreateScope();
        var roleManager = scope.ServiceProvider.GetRequiredService<RoleManager<IdentityRole>>();
        var userManager = scope.ServiceProvider.GetRequiredService<UserManager<AppUser>>();

        foreach (var role in Roles)
        {
            if (!await roleManager.RoleExistsAsync(role))
            {
                EnsureSucceeded(await roleManager.CreateAsync(new IdentityRole(role)), $"создание роли {role}");
            }
        }

        const string adminEmail = "admin@todo.com";
        var admin = await userManager.FindByEmailAsync(adminEmail);
        if (admin is null)
        {
            admin = new AppUser
            {
                UserName = adminEmail,
                Email = adminEmail,
                EmailConfirmed = true,
                FullName = "Администратор"
            };
            EnsureSucceeded(await userManager.CreateAsync(admin, "Admin123!"), "создание администратора");
        }

        if (!await userManager.IsInRoleAsync(admin, "Admin"))
        {
            EnsureSucceeded(await userManager.AddToRoleAsync(admin, "Admin"), "назначение роли Admin");
        }
    }

    private static void EnsureSucceeded(IdentityResult result, string operation)
    {
        if (!result.Succeeded)
        {
            throw new InvalidOperationException(
                $"Ошибка при выполнении операции '{operation}': {string.Join("; ", result.Errors.Select(e => e.Description))}");
        }
    }
}
