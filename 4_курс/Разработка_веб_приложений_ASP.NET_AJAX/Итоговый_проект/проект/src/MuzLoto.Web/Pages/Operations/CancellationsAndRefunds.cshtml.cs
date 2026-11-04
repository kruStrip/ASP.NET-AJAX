using System.Security.Claims;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Mvc.RazorPages;
using Microsoft.EntityFrameworkCore;
using MuzLoto.Web.Data;
using MuzLoto.Web.Domain;
using MuzLoto.Web.Services;

namespace MuzLoto.Web.Pages.Operations;

[Authorize(Roles = "Admin,Manager")]
public sealed class CancellationsAndRefundsModel(
    AppDbContext db,
    IAuthorizationService authorization,
    BookingWorkflow workflow) : PageModel
{
    public List<Booking> CancelledBookings { get; private set; } = new();
    public List<Refund> CompletedRefunds { get; private set; } = new();

    public async Task OnGetAsync(CancellationToken ct)
    {
        var deadline = DateTimeOffset.UtcNow.AddHours(-1);
        // Cancelling an order records the refund as completed immediately, so a returned refund no longer
        // hides the booking: restoring within the hour reverses that record and needs no new payment.
        var cancelledQuery = db.Bookings.Include(x => x.GameSession).ThenInclude(x => x.Game)
            .AsNoTracking().Where(x => x.Status == BookingStatus.Cancelled &&
                x.CancelledAt >= deadline && x.GameSession.CompletedAt == null && x.GameSession.SalesOpen);
        if (!User.IsInRole("Admin"))
        {
            var userId = User.FindFirstValue(ClaimTypes.NameIdentifier);
            cancelledQuery = cancelledQuery.Where(x => x.GameSession.Staff.Any(a => a.UserId == userId));
        }
        CancelledBookings = await cancelledQuery.OrderByDescending(x => x.CancelledAt).Take(100).ToListAsync(ct);

        if (User.IsInRole("Admin"))
        {
            CompletedRefunds = await db.Refunds.Include(x => x.Payment).ThenInclude(x => x.Booking)
                .ThenInclude(x => x.GameSession).ThenInclude(x => x.Game)
                .AsNoTracking().Where(x => x.Status == RefundStatus.Completed)
                .OrderByDescending(x => x.CompletedAt).Take(100).ToListAsync(ct);
        }
    }

    public async Task<IActionResult> OnPostRestoreAsync(Guid id, CancellationToken ct)
    {
        var booking = await db.Bookings.AsNoTracking().FirstOrDefaultAsync(x => x.Id == id, ct);
        if (booking is null) return NotFound();
        if (!(await authorization.AuthorizeAsync(User, booking, BookingPolicies.CanManageBooking)).Succeeded)
            return Forbid();
        try
        {
            await workflow.RestoreCancelledAsync(id, User.FindFirstValue(ClaimTypes.NameIdentifier)!, ct);
            return AjaxResult.Success(this, "Заявка восстановлена.");
        }
        catch (BusinessException ex) { return AjaxResult.Failure(this, ex); }
    }
}
