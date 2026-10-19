using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc.RazorPages;
using Microsoft.EntityFrameworkCore;
using MuzLoto.Web.Data;
using MuzLoto.Web.Domain;

namespace MuzLoto.Web.Pages.Reports;

[Authorize(Roles = "Admin")]
public class IndexModel(AppDbContext db) : PageModel
{
    public int Sold { get; private set; }
    public int Orders { get; private set; }
    public int PaymentsCount { get; private set; }
    public int RefundsCount { get; private set; }
    public int PendingRefundsCount { get; private set; }
    public decimal GrossRevenue { get; private set; }
    public decimal RefundedAmount { get; private set; }
    public decimal PendingRefundAmount { get; private set; }
    public decimal NetRevenue => GrossRevenue - RefundedAmount;
    public decimal AverageCheck => PaymentsCount == 0 ? 0 : GrossRevenue / PaymentsCount;

    public async Task OnGetAsync(CancellationToken ct)
    {
        var active = db.Bookings.Where(x => x.Status == BookingStatus.Confirmed);
        Orders = await active.CountAsync(ct);
        Sold = await active.SumAsync(x => (int?)x.Quantity, ct) ?? 0;
        var payments = db.Payments.Where(x => x.Status == PaymentStatus.Confirmed);
        PaymentsCount = await payments.CountAsync(ct);
        GrossRevenue = await payments.SumAsync(x => (decimal?)x.Amount, ct) ?? 0;
        var completed = db.Refunds.Where(x => x.Status == RefundStatus.Completed);
        RefundsCount = await completed.CountAsync(ct);
        RefundedAmount = await completed.SumAsync(x => (decimal?)x.Amount, ct) ?? 0;
        var pending = db.Refunds.Where(x => x.Status == RefundStatus.Pending);
        PendingRefundsCount = await pending.CountAsync(ct);
        PendingRefundAmount = await pending.SumAsync(x => (decimal?)x.Amount, ct) ?? 0;
    }
}
