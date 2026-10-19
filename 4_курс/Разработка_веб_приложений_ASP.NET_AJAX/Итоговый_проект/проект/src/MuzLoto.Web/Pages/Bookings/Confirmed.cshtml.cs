using System.Security.Claims;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Mvc.RazorPages;
using MuzLoto.Web.Services;
using Microsoft.EntityFrameworkCore;
using MuzLoto.Web.Data;
using MuzLoto.Web.Domain;

namespace MuzLoto.Web.Pages.Bookings;

[Authorize(Roles = "Admin,Manager")]
public class ConfirmedModel(AppDbContext db, StaffAccess access, BookingWorkflow workflow) : PageModel
{
    public List<Booking> Bookings { get; private set; } = new();
    public List<Guid> AccessibleSessionIds { get; private set; } = new();
    public async Task OnGetAsync(CancellationToken ct)
    {
        var query = db.Bookings.Include(x => x.GameSession).ThenInclude(x => x.Game).AsNoTracking()
            .Where(x => x.Status == BookingStatus.Confirmed && x.GameSession.CompletedAt == null);
        if (!User.IsInRole("Admin"))
        {
            var userId = User.FindFirstValue(ClaimTypes.NameIdentifier);
            query = query.Where(x => x.GameSession.Staff.Any(a => a.UserId == userId));
        }
        var scope = db.GameSessions.AsNoTracking().Where(s => s.CompletedAt == null);
        if (!User.IsInRole("Admin"))
        {
            var uid = User.FindFirstValue(ClaimTypes.NameIdentifier);
            scope = scope.Where(s => s.Staff.Any(a => a.UserId == uid));
        }
        AccessibleSessionIds = await scope.Select(s => s.Id).Take(250).ToListAsync(ct);
        Bookings = await query.OrderBy(x => x.GameSession.StartsAt).Take(200).ToListAsync(ct);
    }
    public async Task<IActionResult> OnPostCancelAsync(Guid id, CancellationToken ct)
    {
        var sessionId = await db.Bookings.Where(x => x.Id == id)
            .Select(x => (Guid?)x.GameSessionId).FirstOrDefaultAsync(ct);
        if (!sessionId.HasValue) return NotFound();
        if (!await access.CanManageAsync(User, sessionId.Value, ct)) return Forbid();
        try
        {
            await workflow.CancelAsync(id, User.FindFirstValue(ClaimTypes.NameIdentifier)!, ct);
            return AjaxResult.Success(this, "Покупка отменена, места освобождены. Заявка на возврат денег создана — подтвердите перевод в разделе «Возвраты».");
        }
        catch (BusinessException ex) { return AjaxResult.Failure(this, ex); }
    }
}
