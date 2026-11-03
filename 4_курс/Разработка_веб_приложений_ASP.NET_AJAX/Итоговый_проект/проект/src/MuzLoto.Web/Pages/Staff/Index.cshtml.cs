using System.ComponentModel.DataAnnotations;
using System.Security.Claims;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Identity;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Mvc.RazorPages;
using Microsoft.EntityFrameworkCore;
using MuzLoto.Web.Data;
using MuzLoto.Web.Domain;
using MuzLoto.Web.Services;

namespace MuzLoto.Web.Pages.Staff;

[Authorize(Roles = "Admin")]
public sealed class IndexModel(UserManager<AppUser> users, AppDbContext db, StaffManagement management) : PageModel
{
    public sealed record StaffRow(string Id, string Email, string FullName, string Role, int AssignedGames);
    public List<StaffRow> Employees { get; private set; } = [];
    /// <summary>The signed-in administrator: their own card has no «Сбросить пароль» — they use «Сменить пароль».</summary>
    public string CurrentUserId { get; private set; } = string.Empty;

    [BindProperty] public CreateInput Input { get; set; } = new();
    public sealed class CreateInput
    {
        [Required, StringLength(150, MinimumLength = 2)] public string FullName { get; set; } = string.Empty;
        [Required, EmailAddress, StringLength(254)] public string Email { get; set; } = string.Empty;
        [Required, MinLength(12), DataType(DataType.Password)] public string TemporaryPassword { get; set; } = string.Empty;
        [Required] public string Role { get; set; } = "Manager";
    }

    public async Task OnGetAsync(CancellationToken ct) => await LoadAsync(ct);

    public async Task<IActionResult> OnPostCreateAsync(CancellationToken ct)
    {
        if (!StaffManagement.IsAllowedRole(Input.Role))
            ModelState.AddModelError("Input.Role", "Выберите допустимую роль.");
        if (!ModelState.IsValid) { await LoadAsync(ct); return Page(); }
        try
        {
            await management.CreateAsync(Input.FullName, Input.Email, Input.TemporaryPassword, Input.Role, ct);
            TempData["Notice"] = "Сотрудник создан. Передайте временный пароль по защищённому каналу; на сервере сохраняется только хеш.";
            return RedirectToPage();
        }
        catch (BusinessException ex)
        {
            ModelState.AddModelError(string.Empty, ex.Message);
            await LoadAsync(ct);
            return Page();
        }
    }

    public async Task<IActionResult> OnPostChangeRoleAsync(string userId, string role, CancellationToken ct)
    {
        try
        {
            await management.ChangeRoleAsync(userId, role, User.FindFirstValue(ClaimTypes.NameIdentifier)!, ct);
            TempData["Notice"] = "Роль обновлена. Для продолжения работы сотруднику нужно войти заново.";
        }
        catch (BusinessException ex) { TempData["Error"] = ex.Message; }
        return RedirectToPage();
    }

    /// <summary>A password was forgotten: a new one is generated and shown to the admin once, who passes it on.</summary>
    public async Task<IActionResult> OnPostResetPasswordAsync(string userId, [FromServices] ClientAccounts accounts)
    {
        var user = await users.FindByIdAsync(userId);
        if (user is null) return NotFound();
        // Resetting your own password here would end your session before you could read the new one.
        if (userId == User.FindFirstValue(ClaimTypes.NameIdentifier))
        {
            TempData["Error"] = "Свой пароль меняйте сами в «Сменить пароль»: пароль, выданный здесь, вы не увидите — текущая сессия сразу завершится.";
            return RedirectToPage();
        }
        var password = await accounts.ResetPasswordAsync(user);
        var who = await users.IsInRoleAsync(user, "User") ? "клиенту" : "сотруднику";
        TempData["Notice"] = $"Новый пароль для {user.UserName}: {password} — передайте его {who}. Старые сессии завершены, новый пароль можно сменить в «Сменить пароль».";
        return RedirectToPage();
    }

    /// <summary>«Завершить сессии»: the account stays, but every device has to sign in again.</summary>
    public async Task<IActionResult> OnPostEndSessionsAsync(string userId)
    {
        try
        {
            await management.EndSessionsAsync(userId);
            TempData["Notice"] = "Все сессии аккаунта завершены: на каждом устройстве нужно войти заново.";
        }
        catch (BusinessException ex) { TempData["Error"] = ex.Message; }
        return RedirectToPage();
    }

    /// <summary>«Удалить аккаунт»: the account and its sign-ins are gone; bookings stay in the history without an owner.</summary>
    public async Task<IActionResult> OnPostDeleteAsync(string userId, CancellationToken ct)
    {
        try
        {
            await management.DeleteAsync(userId, User.FindFirstValue(ClaimTypes.NameIdentifier)!, ct);
            TempData["Notice"] = "Аккаунт удалён. Заявки и оплаты сохранены в истории, сессии завершены.";
        }
        catch (BusinessException ex) { TempData["Error"] = ex.Message; }
        return RedirectToPage();
    }

    public static string RoleName(string role) => role switch
    {
        "Admin" => "Администратор", "Manager" => "Менеджер", _ => "Пользователь"
    };

    private async Task LoadAsync(CancellationToken ct)
    {
        CurrentUserId = User.FindFirstValue(ClaimTypes.NameIdentifier) ?? string.Empty;
        var all = await users.Users.AsNoTracking().OrderBy(x => x.Email).Take(300).ToListAsync(ct);
        var counts = await db.EventStaffAssignments.AsNoTracking()
            .GroupBy(x => x.UserId).Select(g => new { Id = g.Key, Count = g.Count() })
            .ToDictionaryAsync(x => x.Id, x => x.Count, ct);
        var rows = new List<StaffRow>();
        foreach (var user in all)
        {
            var roles = await users.GetRolesAsync(user);
            rows.Add(new StaffRow(user.Id, user.Email ?? user.UserName ?? "", user.FullName ?? "",
                roles.FirstOrDefault() ?? "User", counts.GetValueOrDefault(user.Id)));
        }
        Employees = rows;
    }
}
