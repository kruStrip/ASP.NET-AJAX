using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Mvc.RazorPages;
using Microsoft.EntityFrameworkCore;
using MuzLoto.Web.Data;
using MuzLoto.Web.Domain;
using MuzLoto.Web.Services;

namespace MuzLoto.Web.Pages;

/// <summary>Payment screen after «К оплате»: what was booked, how much to pay and how (set by an admin on /payment-settings).</summary>
public class PayModel(AppDbContext db, PaymentLinks links) : PageModel
{
    public Booking Booking { get; private set; } = null!;
    public Domain.PaymentSettings? Settings { get; private set; }
    public decimal Total => Booking.TicketPriceSnapshot * Booking.Quantity;
    public string LocalStart => ScheduleTime.Format(Booking.GameSession.StartsAt, Booking.GameSession.TimeZoneId);

    public async Task<IActionResult> OnGetAsync(string token, CancellationToken ct)
    {
        if (links.Read(token) is not { } id) return NotFound();
        var booking = await db.Bookings.AsNoTracking().Include(x => x.GameSession).ThenInclude(x => x.Game)
            .SingleOrDefaultAsync(x => x.Id == id, ct);
        if (booking is null) return NotFound();
        Booking = booking;
        Settings = await db.PaymentSettings.AsNoTracking().SingleOrDefaultAsync(ct);
        // The page shows personal data: keep it out of shared caches and search engines.
        Response.Headers.CacheControl = "no-store";
        Response.Headers["X-Robots-Tag"] = "noindex";
        return Page();
    }
}
