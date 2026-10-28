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

    private async Task LoadAsync(CancellationToken ct)
    {
        var all = await users.Users.AsNoTracking().OrderBy(x => x.Email).Take(300).ToListAsync(ct);
        var counts = await db.EventStaffAssignments.AsNoTracking()
            .GroupBy(x => x.UserId).Select(g => new { Id = g.Key, Count = g.Count() })
            .ToDictionaryAsync(x => x.Id, x => x.Count, ct);
        var rows = new List<StaffRow>();
        foreach (var user in all)
        {
            var roles = await users.GetRolesAsync(user);
            rows.Add(new StaffRow(user.Id, user.Email ?? "", user.FullName ?? "",
                roles.FirstOrDefault() ?? "User", counts.GetValueOrDefault(user.Id)));
        }
        Employees = rows;
    }
}
