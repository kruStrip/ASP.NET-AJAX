using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;
using MuzLoto.Web.Data;
using MuzLoto.Web.Domain;
using MuzLoto.Web.Services;

namespace MuzLoto.Web.Pages.Bookings;

[Authorize(Roles = "Admin,Manager")]
public class PendingModel(AppDbContext db, IAuthorizationService authorization, BookingWorkflow workflow)
    : BookingListBase(db, authorization)
{
    public List<Booking> Bookings { get; private set; } = new();

    public async Task OnGetAsync(CancellationToken ct)
    {
        Bookings = await ScopedBookings().Where(x => x.Status == BookingStatus.Pending)
            .OrderByDescending(x => x.CreatedAt).Take(100).ToListAsync(ct);
        await LoadSessionsAsync(Bookings, ct);
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
            await workflow.UpdatePendingAsync(id, booking!.TeamName, booking.CustomerName, booking.Telegram, quantity, ct);
            return $"Количество изменено: {quantity} бил., к оплате {quantity * booking.TicketPriceSnapshot:N0} ₽.";
        });
    }
}
