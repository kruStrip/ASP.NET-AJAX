using System.Security.Claims;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Mvc.RazorPages;
using Microsoft.EntityFrameworkCore;
using MuzLoto.Web.Data;
using MuzLoto.Web.Domain;
using MuzLoto.Web.Services;

namespace MuzLoto.Web.Pages.Bookings;

[Authorize]
public class MineModel(AppDbContext db, IAuthorizationService authorization, CoverImages files,
    Microsoft.AspNetCore.SignalR.IHubContext<MuzLoto.Web.Hubs.BookingHub> hub) : PageModel
{
    public List<Booking> Bookings { get; private set; } = new();
    public Domain.PaymentSettings? Settings { get; private set; }
    /// <summary>"Изменить" is shown only where the CanEditBooking policy allows it.</summary>
    public HashSet<Guid> Editable { get; } = new();

    public async Task OnGetAsync(CancellationToken ct)
    {
        // Ownership comes from the authenticated cookie, never from the request.
        var userId = User.FindFirstValue(ClaimTypes.NameIdentifier);
        Bookings = await db.Bookings.Include(x => x.GameSession).ThenInclude(x => x.Game).AsNoTracking()
            .Where(x => x.OwnerUserId == userId)
            .OrderByDescending(x => x.CreatedAt).Take(100).ToListAsync(ct);
        foreach (var booking in Bookings)
            if ((await authorization.AuthorizeAsync(User, booking, BookingPolicies.CanEditBooking)).Succeeded)
                Editable.Add(booking.Id);
        Settings = await db.PaymentSettings.AsNoTracking().SingleOrDefaultAsync(ct);
    }

    /// <summary>The owner uploads a payment receipt for an unpaid booking; staff see it in «Подтвердить игру».</summary>
    public async Task<IActionResult> OnPostReceiptAsync(Guid id, IFormFile? receipt, CancellationToken ct)
    {
        var userId = User.FindFirstValue(ClaimTypes.NameIdentifier);
        var booking = await db.Bookings.SingleOrDefaultAsync(x => x.Id == id && x.OwnerUserId == userId, ct);
        if (booking is null) return NotFound();
        var anchor = $"booking-{id:N}";
        if (booking.Status != BookingStatus.Pending) { TempData["Error"] = "Чек нужен только для неоплаченной заявки."; return RedirectToPage(null, null, anchor); }
        if (receipt is null) { TempData["Error"] = "Выберите файл с чеком."; return RedirectToPage(null, null, anchor); }
        try { await files.SaveReceiptAsync(booking, receipt, ct); }
        catch (BusinessException ex) { TempData["Error"] = ex.Message; return RedirectToPage(null, null, anchor); }
        await ReceiptNotice.SendAsync(hub, booking);
        TempData["Notice"] = "Чек отправлен. Менеджер проверит оплату и подтвердит бронь.";
        return RedirectToPage(null, null, anchor);
    }

    public static string StatusText(BookingStatus status) => status switch
    {
        BookingStatus.Pending => "Ожидает подтверждения оплаты",
        BookingStatus.Confirmed => "Оплата подтверждена",
        _ => "Отменена"
    };

    public static string StatusClass(BookingStatus status) => status switch
    {
        BookingStatus.Pending => "pending",
        BookingStatus.Confirmed => "confirmed",
        _ => "cancelled"
    };
}
