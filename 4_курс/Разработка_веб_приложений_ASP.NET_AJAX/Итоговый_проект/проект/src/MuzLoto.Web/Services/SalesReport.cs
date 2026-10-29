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

/// <summary>Sales of one game (all its dates) or of one date: confirmed orders only.</summary>
public sealed record SalesRow(string Title, int Tickets, decimal Revenue, int Orders);

/// <param name="Date">Local start time of the session in its own time zone.</param>
public sealed record DateSalesRow(Guid SessionId, string Title, DateTime Date, string TimeZoneId, int Tickets, decimal Revenue, int Orders);

/// <summary>Operational view of an upcoming date: what is sold, what is free, what still waits for payment.</summary>
public sealed record OperationalRow(Guid SessionId, string Title, DateTime Date, string TimeZoneId,
    int Capacity, int Sold, int Remaining, int PendingRequests, int PendingTickets, bool SalesOpen);

/// <summary>Optional period by session start (inclusive dates, UTC).</summary>
public sealed record ReportPeriod(DateOnly? From, DateOnly? To)
{
    public static readonly ReportPeriod All = new(null, null);
}

/// <summary>One source of sales figures for the Razor report and the REST API. Unconfirmed requests are never counted as sales.</summary>
public sealed class SalesReport(AppDbContext db)
{
    private sealed record PaidOrder(Guid SessionId, Guid GameId, string Title, DateTimeOffset StartsAt, string TimeZoneId,
        int Quantity, decimal Paid, decimal Refunded);

    /// <summary>Confirmed orders in scope, each with its payment and completed refunds (net revenue = paid − refunded).</summary>
    private async Task<List<PaidOrder>> PaidOrdersAsync(ClaimsPrincipal user, ReportPeriod period, CancellationToken ct)
    {
        var bookings = Scope(user, db.Bookings.AsNoTracking()).Where(x => x.Status == BookingStatus.Confirmed && x.Payment != null);
        if (period.From is { } from)
        {
            var start = new DateTimeOffset(from.ToDateTime(TimeOnly.MinValue), TimeSpan.Zero);
            bookings = bookings.Where(x => x.GameSession.StartsAt >= start);
        }
        if (period.To is { } to)
        {
            var end = new DateTimeOffset(to.AddDays(1).ToDateTime(TimeOnly.MinValue), TimeSpan.Zero);
            bookings = bookings.Where(x => x.GameSession.StartsAt < end);
        }
        return await bookings.Select(x => new PaidOrder(
                x.GameSessionId, x.GameSession.GameId, x.GameSession.Game.Title, x.GameSession.StartsAt, x.GameSession.TimeZoneId,
                x.Quantity, x.Payment!.Amount,
                x.Payment.Refunds.Where(r => r.Status == RefundStatus.Completed).Sum(r => (decimal?)r.Amount) ?? 0))
            .ToListAsync(ct);
    }

    public async Task<IReadOnlyList<SalesRow>> ByGameAsync(ClaimsPrincipal user, ReportPeriod period, CancellationToken ct = default) =>
        (await PaidOrdersAsync(user, period, ct))
            .GroupBy(x => x.GameId)
            .Select(g => new SalesRow(g.First().Title, g.Sum(x => x.Quantity), g.Sum(x => x.Paid - x.Refunded), g.Count()))
            .OrderByDescending(x => x.Revenue).ThenBy(x => x.Title).ToList();

    public async Task<IReadOnlyList<DateSalesRow>> ByDateAsync(ClaimsPrincipal user, ReportPeriod period, CancellationToken ct = default) =>
        (await PaidOrdersAsync(user, period, ct))
            .GroupBy(x => x.SessionId)
            .Select(g =>
            {
                var s = g.First();
                return new DateSalesRow(s.SessionId, s.Title, ScheduleTime.ToLocal(s.StartsAt, s.TimeZoneId), s.TimeZoneId,
                    g.Sum(x => x.Quantity), g.Sum(x => x.Paid - x.Refunded), g.Count());
            })
            .OrderBy(x => x.Date).ToList();

    /// <summary>Upcoming, not completed, not archived dates: seats and unpaid requests (requests never take seats).</summary>
    public async Task<IReadOnlyList<OperationalRow>> OperationalAsync(ClaimsPrincipal user, CancellationToken ct = default)
    {
        var now = DateTimeOffset.UtcNow;
        var sessions = db.GameSessions.AsNoTracking().Where(x => x.StartsAt > now && x.CompletedAt == null && x.ArchivedAt == null);
        if (!user.IsInRole("Admin"))
        {
            var uid = user.FindFirstValue(ClaimTypes.NameIdentifier);
            sessions = sessions.Where(x => x.Staff.Any(a => a.UserId == uid));
        }
        var rows = await sessions.OrderBy(x => x.StartsAt).Select(x => new
        {
            x.Id, x.Game.Title, x.StartsAt, x.TimeZoneId, x.Capacity, x.SalesOpen,
            Sold = x.Bookings.Where(b => b.Status == BookingStatus.Confirmed).Sum(b => (int?)b.Quantity) ?? 0,
            PendingRequests = x.Bookings.Count(b => b.Status == BookingStatus.Pending),
            PendingTickets = x.Bookings.Where(b => b.Status == BookingStatus.Pending).Sum(b => (int?)b.Quantity) ?? 0
        }).Take(200).ToListAsync(ct);
        return rows.Select(x => new OperationalRow(x.Id, x.Title, ScheduleTime.ToLocal(x.StartsAt, x.TimeZoneId), x.TimeZoneId,
            x.Capacity, x.Sold, Math.Max(0, x.Capacity - x.Sold), x.PendingRequests, x.PendingTickets, x.SalesOpen)).ToList();
    }

    private static IQueryable<Booking> Scope(ClaimsPrincipal user, IQueryable<Booking> bookings)
    {
        if (user.IsInRole("Admin")) return bookings;
        var uid = user.FindFirstValue(ClaimTypes.NameIdentifier);
        return bookings.Where(x => x.GameSession.Staff.Any(a => a.UserId == uid));
    }

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
        // A reduced paid order keeps its payment, but money already returned for the reduction is not revenue.
        var confirmedRevenue = (await payments.Where(x => x.Booking.Status == BookingStatus.Confirmed)
            .SumAsync(x => (decimal?)x.Amount, ct) ?? 0)
            - (await refunds.Where(x => x.Status == RefundStatus.Completed && x.Payment.Booking.Status == BookingStatus.Confirmed)
                .SumAsync(x => (decimal?)x.Amount, ct) ?? 0);
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
