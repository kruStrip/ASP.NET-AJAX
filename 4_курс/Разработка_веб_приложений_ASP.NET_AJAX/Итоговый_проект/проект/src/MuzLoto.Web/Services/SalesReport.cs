using System.Security.Claims;
using Microsoft.EntityFrameworkCore;
using MuzLoto.Web.Data;
using MuzLoto.Web.Domain;

namespace MuzLoto.Web.Services;

/// <param name="ConfirmedRevenue">Payments of bookings that are currently confirmed (the specification's "подтверждённая выручка").</param>
/// <param name="GrossPayments">All confirmed payments ever received, including later cancelled orders.</param>
/// <param name="NetRevenue">Gross payments minus refunds actually paid out.</param>
public sealed record SalesSummary(
    int TicketsSold, int ConfirmedOrders, decimal ConfirmedRevenue,
    decimal AverageCheck, decimal AverageTicketsPerOrder, int PendingRequests,
    int PaymentsCount, decimal GrossPayments, decimal Refunded, decimal NetRevenue,
    int PendingRefundsCount, decimal PendingRefundAmount);

/// <summary>One source of sales figures for the Razor report and the REST API. Unconfirmed requests are never counted as sales.</summary>
public sealed class SalesReport(AppDbContext db)
{
    public async Task<SalesSummary> SummaryAsync(ClaimsPrincipal user, CancellationToken ct = default)
    {
        var bookings = db.Bookings.AsNoTracking();
        var payments = db.Payments.AsNoTracking().Where(x => x.Status == PaymentStatus.Confirmed);
        var refunds = db.Refunds.AsNoTracking();
        if (!user.IsInRole("Admin"))
        {
            // Managers see only the games assigned to them.
            var uid = user.FindFirstValue(ClaimTypes.NameIdentifier);
            bookings = bookings.Where(x => x.GameSession.Staff.Any(a => a.UserId == uid));
            payments = payments.Where(x => x.Booking.GameSession.Staff.Any(a => a.UserId == uid));
            refunds = refunds.Where(x => x.Payment.Booking.GameSession.Staff.Any(a => a.UserId == uid));
        }

        var confirmed = bookings.Where(x => x.Status == BookingStatus.Confirmed);
        var orders = await confirmed.CountAsync(ct);
        var tickets = await confirmed.SumAsync(x => (int?)x.Quantity, ct) ?? 0;
        var confirmedRevenue = await payments.Where(x => x.Booking.Status == BookingStatus.Confirmed)
            .SumAsync(x => (decimal?)x.Amount, ct) ?? 0;
        var gross = await payments.SumAsync(x => (decimal?)x.Amount, ct) ?? 0;
        var refunded = await refunds.Where(x => x.Status == RefundStatus.Completed).SumAsync(x => (decimal?)x.Amount, ct) ?? 0;
        var pendingRefunds = refunds.Where(x => x.Status == RefundStatus.Pending);

        return new SalesSummary(
            TicketsSold: tickets,
            ConfirmedOrders: orders,
            ConfirmedRevenue: confirmedRevenue,
            AverageCheck: orders == 0 ? 0 : Math.Round(confirmedRevenue / orders, 2),
            AverageTicketsPerOrder: orders == 0 ? 0 : Math.Round((decimal)tickets / orders, 2),
            PendingRequests: await bookings.CountAsync(x => x.Status == BookingStatus.Pending, ct),
            PaymentsCount: await payments.CountAsync(ct),
            GrossPayments: gross,
            Refunded: refunded,
            NetRevenue: gross - refunded,
            PendingRefundsCount: await pendingRefunds.CountAsync(ct),
            PendingRefundAmount: await pendingRefunds.SumAsync(x => (decimal?)x.Amount, ct) ?? 0);
    }
}
