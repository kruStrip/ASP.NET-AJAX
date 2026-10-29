using System.Data;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Mvc.RazorPages;
using Microsoft.EntityFrameworkCore;
using MuzLoto.Web.Data;
using MuzLoto.Web.Domain;
using MuzLoto.Web.Services;

namespace MuzLoto.Web.Pages.Events;

/// <summary>One date of a game: card, other dates, bookings summary; admin manages the cover, archive and deletion.</summary>
[Authorize(Roles = "Admin,Manager")]
public class DetailsModel(AppDbContext db, StaffAccess access, CoverImages covers, SessionCrud sessions) : PageModel
{
    public sealed record BookingRow(Guid Id, string TeamName, string CustomerName, int Quantity, decimal Total, BookingStatus Status);

    public GameSession Session { get; private set; } = null!;
    public string? CoverUrl { get; private set; }
    public int Sold { get; private set; }
    public int Remaining => Math.Max(0, Session.Capacity - Sold);
    public List<GameSession> OtherDates { get; private set; } = new();
    public List<BookingRow> Bookings { get; private set; } = new();
    public bool IsAdmin => User.IsInRole("Admin");

    public async Task<IActionResult> OnGetAsync(Guid id, CancellationToken ct)
    {
        if (!await access.CanManageAsync(User, id, ct))
            return await db.GameSessions.AnyAsync(x => x.Id == id, ct) ? Forbid() : NotFound();
        return await LoadAsync(id, ct) ? Page() : NotFound();
    }

    public async Task<IActionResult> OnPostCoverAsync(Guid id, IFormFile? cover, CancellationToken ct)
    {
        if (!IsAdmin) return Forbid();
        if (cover is null) return Fail("Выберите файл изображения.");
        try { await covers.SaveAsync(id, cover, ct); }
        catch (BusinessException ex) { return Fail(ex.Message); }
        return Done("Обложка сохранена.");
    }

    public async Task<IActionResult> OnPostArchiveAsync(Guid id, CancellationToken ct)
    {
        if (!IsAdmin) return Forbid();
        await using var tx = await db.Database.BeginTransactionAsync(IsolationLevel.Serializable, ct);
        var s = await db.GameSessions.SingleOrDefaultAsync(x => x.Id == id, ct);
        if (s is null) return NotFound();
        // Unpaid requests must be handled first (completing the game deletes them) so nothing disappears silently.
        if (await db.Bookings.AnyAsync(x => x.GameSessionId == id && x.Status == BookingStatus.Pending, ct))
            return Fail("Есть неподтверждённые заявки. Сначала завершите игру или обработайте заявки.");
        s.ArchivedAt ??= DateTimeOffset.UtcNow;
        s.SalesOpen = false;
        await db.SaveChangesAsync(ct);
        await tx.CommitAsync(ct);
        return Done("Игра перенесена в архив. Продажи и история сохранены.");
    }

    public async Task<IActionResult> OnPostUnarchiveAsync(Guid id, CancellationToken ct)
    {
        if (!IsAdmin) return Forbid();
        var s = await db.GameSessions.SingleOrDefaultAsync(x => x.Id == id, ct);
        if (s is null) return NotFound();
        s.ArchivedAt = null; // sales stay closed until reopened in the edit form
        await db.SaveChangesAsync(ct);
        return Done("Игра возвращена из архива. Продажи закрыты — откройте их при редактировании.");
    }

    public async Task<IActionResult> OnPostDeleteAsync(Guid id, CancellationToken ct)
    {
        if (!IsAdmin) return Forbid();
        try { await sessions.DeleteAsync(id, ct); }
        catch (BusinessException ex) { return Fail(ex.Message); }
        TempData["Notice"] = "Дата удалена.";
        return RedirectToPage("/Events/Index");
    }

    private IActionResult Done(string message) { TempData["Notice"] = message; return RedirectToPage(); }
    private IActionResult Fail(string message) { TempData["Error"] = message; return RedirectToPage(); }

    private async Task<bool> LoadAsync(Guid id, CancellationToken ct)
    {
        var s = await db.GameSessions.AsNoTracking().Include(x => x.Game).Include(x => x.Images)
            .SingleOrDefaultAsync(x => x.Id == id, ct);
        if (s is null) return false;
        Session = s;
        var cover = s.Images.OrderBy(x => x.SortOrder).FirstOrDefault();
        CoverUrl = cover is null ? null : CoverImages.Url(cover);
        Sold = await db.Bookings.Where(x => x.GameSessionId == id && x.Status == BookingStatus.Confirmed)
            .SumAsync(x => (int?)x.Quantity, ct) ?? 0;
        var others = db.GameSessions.AsNoTracking().Where(x => x.GameId == s.GameId && x.Id != id);
        if (!IsAdmin)
        {
            var uid = User.FindFirst(System.Security.Claims.ClaimTypes.NameIdentifier)?.Value;
            others = others.Where(x => x.Staff.Any(a => a.UserId == uid));
        }
        OtherDates = await others.OrderBy(x => x.StartsAt).ToListAsync(ct);
        Bookings = await db.Bookings.AsNoTracking().Where(x => x.GameSessionId == id && x.Status != BookingStatus.Cancelled)
            .OrderBy(x => x.Status).ThenByDescending(x => x.CreatedAt)
            .Select(x => new BookingRow(x.Id, x.TeamName, x.CustomerName, x.Quantity, x.Quantity * x.TicketPriceSnapshot, x.Status))
            .Take(200).ToListAsync(ct);
        return true;
    }
}
