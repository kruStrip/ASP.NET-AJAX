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
public class PendingModel(AppDbContext db, IAuthorizationService authorization, BookingWorkflow workflow) : PageModel
{
    public List<Booking> Bookings { get; private set; } = new();
    public List<Guid> AccessibleSessionIds { get; private set; } = new();
    public async Task OnGetAsync(CancellationToken ct)
    {
        var query = db.Bookings.Include(x => x.GameSession).ThenInclude(x => x.Game).AsNoTracking()
            .Where(x => x.Status == BookingStatus.Pending);
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
        Bookings = await query.OrderByDescending(x => x.CreatedAt).Take(100).ToListAsync(ct);
    }
    public async Task<IActionResult> OnPostConfirmAsync(Guid id, CancellationToken ct)
    {
        var booking = await db.Bookings.AsNoTracking().FirstOrDefaultAsync(x => x.Id == id, ct);
        if (booking is null) return NotFound();
        if (!(await authorization.AuthorizeAsync(User, booking, BookingPolicies.CanManageBooking)).Succeeded)
            return Forbid();
        try
        {
            await workflow.ConfirmAsync(id, User.FindFirstValue(ClaimTypes.NameIdentifier)!, ct);
            return AjaxResult.Success(this, "Оплата подтверждена, места списаны.");
        }
        catch (BusinessException ex) { return AjaxResult.Failure(this, ex); }
    }
    public async Task<IActionResult> OnPostCancelAsync(Guid id, CancellationToken ct)
    {
        var booking = await db.Bookings.AsNoTracking().FirstOrDefaultAsync(x => x.Id == id, ct);
        if (booking is null) return NotFound();
        if (!(await authorization.AuthorizeAsync(User, booking, BookingPolicies.CanManageBooking)).Succeeded)
            return Forbid();
        try
        {
            await workflow.CancelAsync(id, User.FindFirstValue(ClaimTypes.NameIdentifier)!, ct);
            return AjaxResult.Success(this, "Заявка отменена.");
        }
        catch (BusinessException ex) { return AjaxResult.Failure(this, ex); }
    }
}
