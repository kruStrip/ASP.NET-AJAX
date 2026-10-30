using System.Security.Claims;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Mvc.RazorPages;
using Microsoft.EntityFrameworkCore;
using MuzLoto.Web.Data;
using MuzLoto.Web.Domain;
using MuzLoto.Web.Services;

namespace MuzLoto.Web.Pages.Events;

[Authorize(Roles = "Admin,Manager")]
public class IndexModel(AppDbContext db, StaffAccess access, BookingWorkflow workflow) : PageModel
{
    public sealed record SessionRow(GameSession Session, int Reserved, int Remaining, string? CoverUrl);
    public static readonly (string Value, string Label)[] Filters =
    [
        ("active", "Актуальные"), ("open", "Продажа открыта"), ("closed", "Продажа закрыта"),
        ("completed", "Прошедшие"), ("archived", "Архив"), ("all", "Все")
    ];

    [BindProperty(SupportsGet = true)] public string? Q { get; set; }
    [BindProperty(SupportsGet = true)] public string Status { get; set; } = "active";
    public List<SessionRow> Sessions { get; private set; } = new();

    public async Task OnGetAsync(CancellationToken ct)
    {
        var q = db.GameSessions.Include(x => x.Game).Include(x => x.Images).AsNoTracking();
        // The agreed specification keeps completed games in the operational view for 3 days.
        var cutoff = DateTimeOffset.UtcNow.AddDays(-3);
        q = Status switch
        {
            "open" => q.Where(x => x.ArchivedAt == null && x.CompletedAt == null && x.SalesOpen),
            "closed" => q.Where(x => x.ArchivedAt == null && x.CompletedAt == null && !x.SalesOpen),
            "completed" => q.Where(x => x.ArchivedAt == null && x.CompletedAt != null),
            "archived" => q.Where(x => x.ArchivedAt != null),
            "all" => q,
            _ => q.Where(x => x.ArchivedAt == null && (x.CompletedAt == null || x.CompletedAt >= cutoff))
        };
        if (!string.IsNullOrWhiteSpace(Q))
        {
            var term = "%" + Q.Trim().Replace("\\", "\\\\").Replace("%", "\\%").Replace("_", "\\_") + "%";
            q = q.Where(x => EF.Functions.ILike(x.Game.Title, term) || EF.Functions.ILike(x.Venue, term));
        }
        var bq = db.Bookings.AsQueryable();
        if (!User.IsInRole("Admin"))
        {
            var userId = User.FindFirstValue(ClaimTypes.NameIdentifier);
            q = q.Where(x => x.Staff.Any(a => a.UserId == userId));
            bq = bq.Where(x => x.GameSession.Staff.Any(a => a.UserId == userId));
        }
        var sessions = await q.OrderBy(x => x.StartsAt).Take(200).ToListAsync(ct);
        var ids = sessions.Select(x => x.Id).ToArray();
        var reserved = await bq.Where(x => ids.Contains(x.GameSessionId) && x.Status == BookingStatus.Confirmed)
            .GroupBy(x => x.GameSessionId)
            .Select(g => new { SessionId = g.Key, Tickets = g.Sum(x => x.Quantity) })
            .ToDictionaryAsync(x => x.SessionId, x => x.Tickets, ct);
        Sessions = sessions.Select(x =>
        {
            var sold = reserved.GetValueOrDefault(x.Id);
            var cover = x.Images.OrderBy(i => i.SortOrder).FirstOrDefault();
            return new SessionRow(x, sold, x.Capacity - sold, cover is null ? null : CoverImages.Url(cover));
        }).ToList();
    }

    /// <summary>«Снять с публикации»: the game is finished and leaves the booking form (same as the former «Игра прошла»).</summary>
    public async Task<IActionResult> OnPostUnpublishAsync(Guid id, CancellationToken ct)
    {
        if (!await access.CanManageAsync(User, id, ct)) return Forbid();
        try
        {
            await workflow.CompleteSessionAsync(id, ct);
            TempData["Notice"] = "Игра снята с публикации. Неоплаченные заявки удалены, оплаченные продажи сохранены.";
        }
        catch (BusinessException ex) { TempData["Error"] = ex.Message; }
        return RedirectToPage(new { Q, Status });
    }
}
