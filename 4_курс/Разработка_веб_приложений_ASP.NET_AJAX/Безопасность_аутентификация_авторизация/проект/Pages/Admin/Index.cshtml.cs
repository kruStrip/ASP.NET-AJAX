using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Identity;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Mvc.RazorPages;
using Microsoft.EntityFrameworkCore;
using SecureTodo.Models;

namespace SecureTodo.Pages.Admin;

[Authorize(Roles = "Admin")]
public class IndexModel(UserManager<AppUser> userManager) : PageModel
{
    public sealed record UserRow(string Id, string FullName, string? Email, IReadOnlyList<string> Roles);
    public List<UserRow> Users { get; private set; } = [];
    public string[] AvailableRoles { get; } = ["Admin", "Author", "User"];
    [TempData] public string? Message { get; set; }
    [TempData] public bool HasError { get; set; }

    public async Task OnGetAsync() => await LoadUsersAsync();

    public async Task<IActionResult> OnPostAsync(string userId, string role)
    {
        if (!AvailableRoles.Contains(role)) return BadRequest();
        var target = await userManager.FindByIdAsync(userId);
        var current = await userManager.GetUserAsync(User);
        if (target is null || current is null) return NotFound();
        var oldRoles = await userManager.GetRolesAsync(target);
        if (target.Id == current.Id && oldRoles.Contains("Admin") && role != "Admin")
        {
            Message = "Нельзя снять роль Admin с самого себя.";
            HasError = true;
            return RedirectToPage();
        }

        var remove = await userManager.RemoveFromRolesAsync(target, oldRoles);
        if (!remove.Succeeded)
        {
            Message = string.Join("; ", remove.Errors.Select(e => e.Description));
            HasError = true;
            return RedirectToPage();
        }
        var add = await userManager.AddToRoleAsync(target, role);
        if (!add.Succeeded)
        {
            await userManager.AddToRolesAsync(target, oldRoles);
            Message = string.Join("; ", add.Errors.Select(e => e.Description));
            HasError = true;
            return RedirectToPage();
        }
        Message = $"Пользователю {target.Email} назначена роль {role}.";
        HasError = false;
        return RedirectToPage();
    }

    private async Task LoadUsersAsync()
    {
        foreach (var user in await userManager.Users.OrderBy(u => u.Email).ToListAsync())
            Users.Add(new UserRow(user.Id, user.FullName, user.Email, (await userManager.GetRolesAsync(user)).ToList()));
    }
}
