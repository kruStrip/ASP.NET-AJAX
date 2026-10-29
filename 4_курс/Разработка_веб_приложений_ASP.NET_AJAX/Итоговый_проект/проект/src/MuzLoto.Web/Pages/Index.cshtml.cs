using Microsoft.AspNetCore.Authorization;
using System.Security.Claims;
using Microsoft.AspNetCore.Mvc.RazorPages;
using Microsoft.EntityFrameworkCore;
using MuzLoto.Web.Data;
using MuzLoto.Web.Domain;

namespace MuzLoto.Web.Pages;

[Authorize(Roles = "Admin,Manager")]
public class IndexModel(AppDbContext db) : PageModel
{
    public int Upcoming { get; private set; }
    public int Pending { get; private set; }
    public int SoldTickets { get; private set; }
    public decimal Revenue { get; private set; }
    public async Task OnGetAsync(CancellationToken ct)
    {
        var sessions = db.GameSessions.AsQueryable();
        var bookings = db.Bookings.AsQueryable();
        if (!User.IsInRole("Admin"))
        {
            var userId = User.FindFirstValue(ClaimTypes.NameIdentifier);
            sessions = sessions.Where(x => x.Staff.Any(a => a.UserId == userId));
            bookings = bookings.Where(x => x.GameSession.Staff.Any(a => a.UserId == userId));
        }
        Upcoming = await sessions.CountAsync(x => x.CompletedAt == null && x.ArchivedAt == null, ct);
        Pending = await bookings.CountAsync(x => x.Status == BookingStatus.Pending, ct);
        SoldTickets = await bookings.Where(x => x.Status == BookingStatus.Confirmed)
            .SumAsync(x => (int?)x.Quantity, ct) ?? 0;
        var payments = db.Payments.Where(x => x.Status == PaymentStatus.Confirmed);
        if (!User.IsInRole("Admin"))
        {
            var userId = User.FindFirstValue(ClaimTypes.NameIdentifier);
            payments = payments.Where(x => x.Booking.GameSession.Staff.Any(a => a.UserId == userId));
        }
        var gross = await payments.SumAsync(x => (decimal?)x.Amount, ct) ?? 0;
        var refunds = db.Refunds.Where(x => x.Status == RefundStatus.Completed);
        if (!User.IsInRole("Admin"))
        {
            var userId = User.FindFirstValue(ClaimTypes.NameIdentifier);
            refunds = refunds.Where(x => x.Payment.Booking.GameSession.Staff.Any(a => a.UserId == userId));
        }
        var returned = await refunds.SumAsync(x => (decimal?)x.Amount, ct) ?? 0;
        Revenue = gross - returned;
    }
}
