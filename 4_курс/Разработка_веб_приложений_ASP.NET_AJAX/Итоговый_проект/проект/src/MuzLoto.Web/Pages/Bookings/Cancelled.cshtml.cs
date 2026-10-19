using System.Security.Claims;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Mvc.RazorPages;
using Microsoft.EntityFrameworkCore;
using MuzLoto.Web.Data;
using MuzLoto.Web.Domain;
using MuzLoto.Web.Services;

namespace MuzLoto.Web.Pages.Bookings;

[Authorize(Roles = "Admin,Manager")]
public sealed class CancelledModel(AppDbContext db, StaffAccess access, BookingWorkflow workflow) : PageModel
{
    public List<Booking> Bookings { get; private set; } = new();
    public async Task OnGetAsync(CancellationToken ct)
    {
        var deadline = DateTimeOffset.UtcNow.AddHours(-1);
        var query = db.Bookings.Include(x => x.GameSession).ThenInclude(x => x.Game)
            .AsNoTracking().Where(x => x.Status == BookingStatus.Cancelled &&
                x.CancelledAt >= deadline && x.GameSession.CompletedAt == null && x.GameSession.SalesOpen);
        if (!User.IsInRole("Admin"))
        {
            var userId = User.FindFirstValue(ClaimTypes.NameIdentifier);
            query = query.Where(x => x.GameSession.Staff.Any(a => a.UserId == userId));
        }
        Bookings = await query.OrderByDescending(x => x.CancelledAt).Take(100).ToListAsync(ct);
    }

    public async Task<IActionResult> OnPostRestoreAsync(Guid id, CancellationToken ct)
    {
        var sessionId = await db.Bookings.Where(x => x.Id == id)
            .Select(x => (Guid?)x.GameSessionId).FirstOrDefaultAsync(ct);
        if (!sessionId.HasValue) return NotFound();
        if (!await access.CanManageAsync(User, sessionId.Value, ct)) return Forbid();
        try
        {
            await workflow.RestoreCancelledAsync(id,
                User.FindFirstValue(ClaimTypes.NameIdentifier)!, ct);
            return AjaxResult.Success(this, "Заявка восстановлена.");
        }
        catch (BusinessException ex) { return AjaxResult.Failure(this, ex); }
    }
}
