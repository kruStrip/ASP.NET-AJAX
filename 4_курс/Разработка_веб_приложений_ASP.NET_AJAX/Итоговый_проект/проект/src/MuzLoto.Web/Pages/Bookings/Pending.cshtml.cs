using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;
using MuzLoto.Web.Data;
using MuzLoto.Web.Domain;
using MuzLoto.Web.Services;

namespace MuzLoto.Web.Pages.Bookings;

/// <summary>One card on «Нужно подтвердить»: imported requests have no account, so they can never get a receipt.</summary>
public sealed record PendingCard(Booking Booking, int Remaining, bool Imported);

[Authorize(Roles = "Admin,Manager")]
public class PendingModel(AppDbContext db, IAuthorizationService authorization, BookingWorkflow workflow)
    : BookingListBase(db, authorization)
{
    public List<Booking> Bookings { get; private set; } = new();

    /// <summary>Requests carried over from Google Sheets: the customer has no account, so no receipt can arrive.</summary>
    public List<Booking> ImportedWithoutReceipt { get; private set; } = new();

    public async Task OnGetAsync(CancellationToken ct)
    {
        // A request is only actionable for staff once the customer has sent a receipt (decided 2026-11-04):
        // before that there is nothing to check, so it stays out of «Нужно подтвердить».
        var pending = ScopedBookings().Where(x => x.Status == BookingStatus.Pending);
        Bookings = await pending.Where(x => x.ReceiptUploadedAt != null)
            .OrderByDescending(x => x.CreatedAt).Take(100).ToListAsync(ct);
        // Imported requests are the exception (decided 2026-11-04, after the move off Google Sheets): they have
        // no owner account at all, so waiting for a receipt would hide them from staff forever.
        ImportedWithoutReceipt = await pending.Where(x => x.ReceiptUploadedAt == null && x.LegacyId != null)
            .OrderByDescending(x => x.CreatedAt).Take(100).ToListAsync(ct);
        await LoadSessionsAsync(Bookings.Concat(ImportedWithoutReceipt), ct);
    }

    public async Task<IActionResult> OnPostConfirmAsync(Guid id, CancellationToken ct)
    {
        var (_, denied) = await AuthorizeBookingAsync(id, ct);
        return denied ?? await RunAsync(async () =>
        {
            await workflow.ConfirmAsync(id, UserId, ct);
            return "Оплата подтверждена, места списаны.";
        });
    }

    public async Task<IActionResult> OnPostCancelAsync(Guid id, CancellationToken ct)
    {
        var (_, denied) = await AuthorizeBookingAsync(id, ct);
        return denied ?? await RunAsync(async () =>
        {
            await workflow.CancelAsync(id, UserId, ct);
            return "Заявка отменена.";
        });
    }

    /// <summary>Unpaid request: the count changes freely within the remaining seats (no money involved yet).</summary>
    public async Task<IActionResult> OnPostQuantityAsync(Guid id, int quantity, CancellationToken ct)
    {
        var (booking, denied) = await AuthorizeBookingAsync(id, ct);
        if (denied is not null) return denied;
        if (quantity == 0) return await OnPostCancelAsync(id, ct);
        return await RunAsync(async () =>
        {
            await workflow.UpdatePendingAsync(id, booking!.TeamName, booking.CustomerName, booking.Telegram, quantity, UserId, ct);
            return $"Количество изменено: {quantity} бил., к оплате {quantity * booking.TicketPriceSnapshot:N0} ₽.";
        });
    }
}
