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
public class PendingModel(AppDbContext db, StaffAccess access, BookingWorkflow workflow) : PageModel
{
    public List<Booking> Bookings { get; private set; } = new();
    public async Task OnGetAsync(CancellationToken ct)
    {
        var query = db.Bookings.Include(x => x.GameSession).ThenInclude(x => x.Game).AsNoTracking()
            .Where(x => x.Status == BookingStatus.Pending);
        if (!User.IsInRole("Admin"))
        {
            var userId = User.FindFirstValue(ClaimTypes.NameIdentifier);
            query = query.Where(x => x.GameSession.Staff.Any(a => a.UserId == userId));
        }
        Bookings = await query.OrderByDescending(x => x.CreatedAt).Take(100).ToListAsync(ct);
    }
    public async Task<IActionResult> OnPostConfirmAsync(Guid id, CancellationToken ct)
    {
        var sessionId = await db.Bookings.Where(x => x.Id == id).Select(x => (Guid?)x.GameSessionId).FirstOrDefaultAsync(ct);
        if (!sessionId.HasValue) return NotFound();
        if (!await access.CanManageAsync(User, sessionId.Value, ct)) return Forbid();
        try
        {
            await workflow.ConfirmAsync(id, User.FindFirstValue(ClaimTypes.NameIdentifier)!, ct);
            TempData["Notice"] = "Оплата подтверждена, места списаны.";
        }
        catch (BusinessException ex) { TempData["Error"] = ex.Message; }
        return RedirectToPage();
    }
    public async Task<IActionResult> OnPostCancelAsync(Guid id, CancellationToken ct)
    {
        var sessionId = await db.Bookings.Where(x => x.Id == id).Select(x => (Guid?)x.GameSessionId).FirstOrDefaultAsync(ct);
        if (!sessionId.HasValue) return NotFound();
        if (!await access.CanManageAsync(User, sessionId.Value, ct)) return Forbid();
        try
        {
            await workflow.CancelAsync(id, User.FindFirstValue(ClaimTypes.NameIdentifier)!, ct);
            TempData["Notice"] = "Заявка отменена.";
        }
        catch (BusinessException ex) { TempData["Error"] = ex.Message; }
        return RedirectToPage();
    }
}
