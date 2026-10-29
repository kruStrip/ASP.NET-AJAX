using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;
using MuzLoto.Web.Data;
using MuzLoto.Web.Domain;
using MuzLoto.Web.Services;

namespace MuzLoto.Web.Pages.Bookings;

[Authorize(Roles = "Admin,Manager")]
public class ConfirmedModel(AppDbContext db, IAuthorizationService authorization, BookingWorkflow workflow)
    : BookingListBase(db, authorization)
{
    public List<Booking> Bookings { get; private set; } = new();
    public Dictionary<Guid, QuantityChange> AwaitingSurcharge { get; private set; } = new();

    public async Task OnGetAsync(CancellationToken ct)
    {
        Bookings = await ScopedBookings()
            .Where(x => x.Status == BookingStatus.Confirmed && x.GameSession.CompletedAt == null)
            .OrderBy(x => x.GameSession.StartsAt).Take(200).ToListAsync(ct);
        var ids = Bookings.Select(x => x.Id).ToArray();
        AwaitingSurcharge = await Db.QuantityChanges.AsNoTracking()
            .Where(x => ids.Contains(x.BookingId) && x.Status == QuantityChangeStatus.AwaitingPayment)
            .ToDictionaryAsync(x => x.BookingId, ct);
        await LoadSessionsAsync(Bookings, ct);
    }

    public async Task<IActionResult> OnPostCancelAsync(Guid id, CancellationToken ct)
    {
        var (_, denied) = await AuthorizeBookingAsync(id, ct);
        return denied ?? await RunAsync(async () =>
        {
            await workflow.CancelAsync(id, UserId, ct);
            return "Покупка отменена, места освобождены. Заявка на возврат денег создана — подтвердите перевод в разделе «Возвраты».";
        });
    }

    /// <summary>Paid order: fewer tickets free seats and queue a partial refund; more tickets wait for a surcharge; 0 cancels.</summary>
    public async Task<IActionResult> OnPostQuantityAsync(Guid id, int quantity, CancellationToken ct)
    {
        var (_, denied) = await AuthorizeBookingAsync(id, ct);
        if (denied is not null) return denied;
        if (quantity == 0) return await OnPostCancelAsync(id, ct);
        return await RunAsync(async () =>
        {
            var change = await workflow.ChangePaidQuantityAsync(id, quantity, UserId, ct);
            return change switch
            {
                null => "Количество не изменилось.",
                { IsIncrease: true } => $"Ожидается доплата {change.Amount:N0} ₽. Места займутся после подтверждения доплаты.",
                _ => $"Количество уменьшено до {change.NewQuantity}. Места освобождены, создан возврат {change.Amount:N0} ₽ — подтвердите перевод в «Возвратах»."
            };
        });
    }

    public async Task<IActionResult> OnPostSurchargeAsync(Guid id, Guid changeId, bool received, CancellationToken ct)
    {
        var (_, denied) = await AuthorizeBookingAsync(id, ct);
        if (denied is not null) return denied;
        return await RunAsync(async () =>
        {
            var change = await Db.QuantityChanges.AsNoTracking().SingleOrDefaultAsync(x => x.Id == changeId && x.BookingId == id, ct)
                ?? throw new BusinessException("Изменение не найдено.", 404);
            if (!received)
            {
                await workflow.CancelSurchargeAsync(change.Id, UserId, ct);
                return "Изменение отменено, заказ остался прежним.";
            }
            await workflow.ConfirmSurchargeAsync(change.Id, UserId, ct);
            return $"Доплата подтверждена: теперь {change.NewQuantity} бил.";
        });
    }
}
