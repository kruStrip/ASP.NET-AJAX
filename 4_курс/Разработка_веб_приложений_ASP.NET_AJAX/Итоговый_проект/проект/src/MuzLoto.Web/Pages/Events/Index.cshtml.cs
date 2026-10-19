using System.Security.Claims;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc.RazorPages;
using Microsoft.EntityFrameworkCore;
using MuzLoto.Web.Data;
using MuzLoto.Web.Domain;

namespace MuzLoto.Web.Pages.Events;

[Authorize(Roles = "Admin,Manager")]
public class IndexModel(AppDbContext db) : PageModel
{
    public sealed record SessionRow(GameSession Session, int Reserved, int Remaining);
    public List<SessionRow> Sessions { get; private set; } = new();
    public async Task OnGetAsync(CancellationToken ct)
    {
        var q = db.GameSessions.Include(x => x.Game).AsNoTracking().AsQueryable();
        var bq = db.Bookings.AsQueryable();
        if (!User.IsInRole("Admin"))
        {
            var userId = User.FindFirstValue(ClaimTypes.NameIdentifier);
            q = q.Where(x => x.Staff.Any(a => a.UserId == userId));
            bq = bq.Where(x => x.GameSession.Staff.Any(a => a.UserId == userId));
        }
        var sessions = await q.OrderBy(x => x.StartsAt).ToListAsync(ct);
        var reserved = await bq.Where(x => x.Status == BookingStatus.Confirmed)
            .GroupBy(x => x.GameSessionId)
            .Select(g => new { SessionId = g.Key, Tickets = g.Sum(x => x.Quantity) })
            .ToDictionaryAsync(x => x.SessionId, x => x.Tickets, ct);
        Sessions = sessions.Select(x =>
        {
            var sold = reserved.GetValueOrDefault(x.Id);
            return new SessionRow(x, sold, x.Capacity - sold);
        }).ToList();
    }
}
