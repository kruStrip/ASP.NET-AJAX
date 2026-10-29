using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;
using MuzLoto.Web.Data;
using MuzLoto.Web.Domain;

namespace MuzLoto.Web.Pages.Bookings;

/// <summary>Every paid order ever, including completed and archived games: payment, refunds and count changes.</summary>
[Authorize(Roles = "Admin,Manager")]
public class HistoryModel(AppDbContext db, IAuthorizationService authorization) : BookingListBase(db, authorization)
{
    public const int PageSize = 50;
    public sealed record Row(Booking Booking, decimal Paid, decimal Refunded, decimal RefundPending, int Changes);

    [BindProperty(SupportsGet = true)] public int PageNumber { get; set; } = 1;
    public List<Row> Rows { get; private set; } = new();
    public bool HasMore { get; private set; }

    public async Task OnGetAsync(CancellationToken ct)
    {
        PageNumber = Math.Max(1, PageNumber);
        var rows = await ScopedBookings().Where(x => x.Payment != null)
            .OrderByDescending(x => x.Payment!.ConfirmedAt)
            .Skip((PageNumber - 1) * PageSize).Take(PageSize + 1)
            .Select(x => new Row(x, x.Payment!.Amount,
                x.Payment.Refunds.Where(r => r.Status == RefundStatus.Completed).Sum(r => (decimal?)r.Amount) ?? 0,
                x.Payment.Refunds.Where(r => r.Status == RefundStatus.Pending).Sum(r => (decimal?)r.Amount) ?? 0,
                x.QuantityChanges.Count(c => c.Status == QuantityChangeStatus.Applied)))
            .ToListAsync(ct);
        HasMore = rows.Count > PageSize;
        Rows = rows.Take(PageSize).ToList();
    }
}
