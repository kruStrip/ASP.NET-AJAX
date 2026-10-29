using System.Security.Claims;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc.RazorPages;
using Microsoft.EntityFrameworkCore;
using MuzLoto.Web.Data;
using MuzLoto.Web.Domain;
using MuzLoto.Web.Services;

namespace MuzLoto.Web.Pages.Bookings;

[Authorize]
public class MineModel(AppDbContext db, IAuthorizationService authorization) : PageModel
{
    public List<Booking> Bookings { get; private set; } = new();
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
