using System.Data;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Mvc.RazorPages;
using Microsoft.EntityFrameworkCore;
using MuzLoto.Web.Data;
using MuzLoto.Web.Domain;
using Npgsql;

namespace MuzLoto.Web.Pages.Events;

/// <summary>Edits one date and the shared game card. The session keeps its ID, so covers and bookings stay attached.</summary>
[Authorize(Roles = "Admin")]
public class EditModel(AppDbContext db) : PageModel
{
    [BindProperty] public SessionForm Input { get; set; } = new();
    [BindProperty] public bool SalesOpen { get; set; }
    public Guid SessionId { get; private set; }
    public int OtherDates { get; private set; }

    public async Task<IActionResult> OnGetAsync(Guid id, CancellationToken ct)
    {
        var s = await db.GameSessions.Include(x => x.Game).AsNoTracking().FirstOrDefaultAsync(x => x.Id == id, ct);
        if (s is null) return NotFound();
        await DescribeAsync(s, ct);
        Input = SessionForm.From<SessionForm>(s);
        SalesOpen = s.SalesOpen;
        return Page();
    }

    public async Task<IActionResult> OnPostAsync(Guid id, CancellationToken ct)
    {
        var startsAt = Input.Validate(ModelState, requireTitle: true);
        try
        {
            await using var tx = await db.Database.BeginTransactionAsync(IsolationLevel.Serializable, ct);
            var s = await db.GameSessions.Include(x => x.Game).FirstOrDefaultAsync(x => x.Id == id, ct);
            if (s is null) return NotFound();
            await DescribeAsync(s, ct);
            if (!ModelState.IsValid) return Page();
            if (s.CompletedAt.HasValue || s.ArchivedAt.HasValue)
            {
                ModelState.AddModelError(string.Empty, "Завершённую или архивную игру нельзя редактировать.");
                return Page();
            }
            var reserved = await db.Bookings.Where(x => x.GameSessionId == id && x.Status == BookingStatus.Confirmed)
                .SumAsync(x => (int?)x.Quantity, ct) ?? 0;
            if (Input.Capacity < reserved)
            {
                ModelState.AddModelError("Input.Capacity", $"Уже подтверждено билетов: {reserved}.");
                return Page();
            }
            Input.ApplyToGame(s.Game);
            Input.ApplyToSession(s, startsAt);
            s.SalesOpen = SalesOpen;
            await db.SaveChangesAsync(ct);
            await tx.CommitAsync(ct);
        }
        catch (Exception ex) when (ex is PostgresException { SqlState: PostgresErrorCodes.SerializationFailure } ||
                                   ex.InnerException is PostgresException { SqlState: PostgresErrorCodes.SerializationFailure })
        {
            ModelState.AddModelError(string.Empty, "Данные изменились одновременно. Повторите сохранение.");
            return Page();
        }
        TempData["Notice"] = "Данные игры обновлены.";
        return RedirectToPage("/Events/Details", new { id });
    }

    private async Task DescribeAsync(GameSession s, CancellationToken ct)
    {
        SessionId = s.Id;
        OtherDates = await db.GameSessions.CountAsync(x => x.GameId == s.GameId && x.Id != s.Id, ct);
    }
}
