using System.Security.Claims;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Mvc.RazorPages;
using Microsoft.EntityFrameworkCore;
using MuzLoto.Web.Data;
using MuzLoto.Web.Domain;
using MuzLoto.Web.Services;

namespace MuzLoto.Web.Pages;

/// <summary>Payment screen after «К оплате»: what was booked, how much to pay and how (set by an admin on /payment-settings).</summary>
public class PayModel(AppDbContext db, PaymentLinks links, CoverImages files,
    Microsoft.AspNetCore.SignalR.IHubContext<MuzLoto.Web.Hubs.BookingHub> hub) : PageModel
{
    public string Token { get; private set; } = "";
    public bool IsOwner => Booking.OwnerUserId is { } owner && owner == User.FindFirstValue(ClaimTypes.NameIdentifier);
    public Booking Booking { get; private set; } = null!;
    public Domain.PaymentSettings? Settings { get; private set; }
    public decimal Total => Booking.TicketPriceSnapshot * Booking.Quantity;
    public string LocalStart => ScheduleTime.Format(Booking.GameSession.StartsAt, Booking.GameSession.TimeZoneId);

    public async Task<IActionResult> OnGetAsync(string token, CancellationToken ct)
    {
        if (links.Read(token) is not { } id) return NotFound();
        Token = token;
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

    /// <summary>Whoever holds the payment link may upload the receipt (the link is the customer's own secret).</summary>
    public async Task<IActionResult> OnPostReceiptAsync(string token, IFormFile? receipt, CancellationToken ct)
    {
        if (links.Read(token) is not { } id) return NotFound();
        var booking = await db.Bookings.SingleOrDefaultAsync(x => x.Id == id, ct);
        if (booking is null) return NotFound();
        if (booking.Status != BookingStatus.Pending) TempData["Error"] = "Чек нужен только для неоплаченной заявки.";
        else if (receipt is null) TempData["Error"] = "Выберите файл с чеком.";
        else
        {
            try
            {
                await files.SaveReceiptAsync(booking, receipt, ct);
                await ReceiptNotice.SendAsync(hub, booking);
                TempData["Notice"] = "Чек отправлен. Менеджер проверит оплату и подтвердит бронь.";
            }
            catch (BusinessException ex) { TempData["Error"] = ex.Message; }
        }
        return RedirectToPage(new { token });
    }
}
