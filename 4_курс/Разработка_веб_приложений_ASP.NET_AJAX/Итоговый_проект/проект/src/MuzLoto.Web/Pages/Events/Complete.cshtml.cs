using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Mvc.RazorPages;
using Microsoft.EntityFrameworkCore;
using MuzLoto.Web.Data;
using MuzLoto.Web.Domain;
using MuzLoto.Web.Services;

namespace MuzLoto.Web.Pages.Events;

[Authorize(Roles = "Admin,Manager")]
public class CompleteModel(AppDbContext db, StaffAccess access, BookingWorkflow workflow) : PageModel
{
    public List<GameSession> Sessions { get; private set; } = new();
    public async Task OnGetAsync(CancellationToken ct)
    {
        var since = DateTimeOffset.UtcNow.AddDays(-3);
        var query = db.GameSessions.Include(x => x.Game).AsNoTracking()
            .Where(x => x.ArchivedAt == null && (x.CompletedAt == null || x.CompletedAt >= since));
        if (!User.IsInRole("Admin"))
        {
            var userId = User.FindFirst(System.Security.Claims.ClaimTypes.NameIdentifier)?.Value;
            query = query.Where(x => x.Staff.Any(a => a.UserId == userId));
        }
        Sessions = await query.OrderBy(x => x.StartsAt).ToListAsync(ct);
    }
    public async Task<IActionResult> OnPostReopenAsync(Guid id, CancellationToken ct)
    {
        if (!User.IsInRole("Admin")) return Forbid();
        if (!await access.CanManageAsync(User, id, ct)) return Forbid();
        try
        {
            await workflow.ReopenSessionAsync(id, ct);
            return AjaxResult.Success(this,
                "Завершение отменено. Продажи остаются закрытыми; включите их в редактировании игры. Удалённые заявки не восстановлены.");
        }
        catch (BusinessException ex) { return AjaxResult.Failure(this, ex); }
    }

    public async Task<IActionResult> OnPostAsync(Guid id, CancellationToken ct)
    {
        if (!await access.CanManageAsync(User, id, ct)) return Forbid();
        try
        {
            await workflow.CompleteSessionAsync(id, ct);
            return AjaxResult.Success(this, "Игра завершена. Неоплаченные заявки удалены без возможности восстановления.");
        }
        catch (BusinessException ex) { return AjaxResult.Failure(this, ex); }
    }
}
