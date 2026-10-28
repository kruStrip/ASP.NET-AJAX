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
public sealed class AssignmentsModel(AppDbContext db, UserManager<AppUser> users, StaffManagement management) : PageModel
{
    public Guid SessionId { get; private set; }
    public string GameTitle { get; private set; } = "";
    public List<AppUser> Managers { get; private set; } = [];
    public HashSet<string> AssignedIds { get; private set; } = [];

    public async Task<IActionResult> OnGetAsync(Guid sessionId, CancellationToken ct)
    {
        SessionId = sessionId;
        var session = await db.GameSessions.Include(x => x.Game).AsNoTracking()
            .SingleOrDefaultAsync(x => x.Id == sessionId, ct);
        if (session is null) return NotFound();
        GameTitle = session.Game.Title;
        Managers = (await users.GetUsersInRoleAsync("Manager"))
            .OrderBy(x => x.FullName ?? x.Email).ToList();
        AssignedIds = (await db.EventStaffAssignments.AsNoTracking()
            .Where(x => x.GameSessionId == sessionId).Select(x => x.UserId).ToListAsync(ct)).ToHashSet();
        return Page();
    }

    public async Task<IActionResult> OnPostAssignAsync(Guid sessionId, string userId, CancellationToken ct)
    {
        try
        {
            await management.AssignManagerAsync(sessionId, userId, ct);
            TempData["Notice"] = "Менеджер назначен на игру.";
        }
        catch (BusinessException ex) { TempData["Error"] = ex.Message; }
        return RedirectToPage(new { sessionId });
    }

    public async Task<IActionResult> OnPostRemoveAsync(Guid sessionId, string userId, CancellationToken ct)
    {
        await management.RemoveManagerAsync(sessionId, userId, ct);
        TempData["Notice"] = "Доступ менеджера к игре отозван.";
        return RedirectToPage(new { sessionId });
    }
}
