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
    public List<Refund> PendingRefunds { get; private set; } = new();
    public List<Refund> CompletedRefunds { get; private set; } = new();

    public async Task OnGetAsync(CancellationToken ct)
    {
        var deadline = DateTimeOffset.UtcNow.AddHours(-1);
        var cancelledQuery = db.Bookings.Include(x => x.GameSession).ThenInclude(x => x.Game)
            .AsNoTracking().Where(x => x.Status == BookingStatus.Cancelled &&
                x.CancelledAt >= deadline && x.GameSession.CompletedAt == null && x.GameSession.SalesOpen &&
                !db.Refunds.Any(r => r.Payment.BookingId == x.Id && r.Status == RefundStatus.Completed));
        if (!User.IsInRole("Admin"))
        {
            var userId = User.FindFirstValue(ClaimTypes.NameIdentifier);
            cancelledQuery = cancelledQuery.Where(x => x.GameSession.Staff.Any(a => a.UserId == userId));
        }
        CancelledBookings = await cancelledQuery.OrderByDescending(x => x.CancelledAt).Take(100).ToListAsync(ct);

        if (User.IsInRole("Admin"))
        {
            var refundsQuery = db.Refunds.Include(x => x.Payment).ThenInclude(x => x.Booking)
                .ThenInclude(x => x.GameSession).ThenInclude(x => x.Game).AsNoTracking();
            PendingRefunds = await refundsQuery.Where(x => x.Status == RefundStatus.Pending)
                .OrderBy(x => x.RequestedAt).Take(100).ToListAsync(ct);
            CompletedRefunds = await refundsQuery.Where(x => x.Status == RefundStatus.Completed)
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

    public async Task<IActionResult> OnPostCompleteRefundAsync(Guid id, bool confirmTransfer,
        string? transferReference, CancellationToken ct)
    {
        if (!User.IsInRole("Admin"))
            return Forbid();

        if (!confirmTransfer)
            return AjaxResult.Failure(this, new BusinessException(
                "Подтвердите, что деньги действительно перечислены клиенту.", 400));
        try
        {
            await workflow.ConfirmRefundAsync(id,
                User.FindFirstValue(ClaimTypes.NameIdentifier)!, transferReference, ct);
            return AjaxResult.Success(this, "Денежный возврат зафиксирован. Чистые поступления обновлены.");
        }
        catch (BusinessException ex) { return AjaxResult.Failure(this, ex); }
    }
}
