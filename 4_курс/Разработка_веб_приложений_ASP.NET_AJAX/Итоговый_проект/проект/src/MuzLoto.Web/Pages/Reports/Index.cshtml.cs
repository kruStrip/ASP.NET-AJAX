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
    public decimal Revenue { get; private set; }
    public decimal AverageCheck => Orders == 0 ? 0 : Revenue / Orders;
    public async Task OnGetAsync(CancellationToken ct)
    {
        // Operational sales only. Payment records remain preserved even for cancelled bookings.
        var q = db.Bookings.Where(x => x.Status == BookingStatus.Confirmed);
        Orders = await q.CountAsync(ct);
        Sold = await q.SumAsync(x => (int?)x.Quantity, ct) ?? 0;
        Revenue = await q.SumAsync(x => (decimal?)(x.Quantity * x.TicketPriceSnapshot), ct) ?? 0;
    }
}
