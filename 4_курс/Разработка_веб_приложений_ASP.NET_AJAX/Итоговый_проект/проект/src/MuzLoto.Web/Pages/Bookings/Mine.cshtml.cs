using System.Security.Claims;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc.RazorPages;
using Microsoft.EntityFrameworkCore;
using MuzLoto.Web.Data;
using MuzLoto.Web.Domain;

namespace MuzLoto.Web.Pages.Bookings;

[Authorize]
public class MineModel(AppDbContext db) : PageModel
{
    public List<Booking> Bookings { get; private set; } = new();

    public async Task OnGetAsync(CancellationToken ct)
    {
        // Ownership comes from the authenticated cookie, never from the request.
        var userId = User.FindFirstValue(ClaimTypes.NameIdentifier);
        Bookings = await db.Bookings.Include(x => x.GameSession).ThenInclude(x => x.Game).AsNoTracking()
            .Where(x => x.OwnerUserId == userId)
            .OrderByDescending(x => x.CreatedAt).Take(100).ToListAsync(ct);
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
