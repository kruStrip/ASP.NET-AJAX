using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Mvc.RazorPages;
using Microsoft.EntityFrameworkCore;
using MuzLoto.Web.Data;
using MuzLoto.Web.Domain;

namespace MuzLoto.Web.Pages.Events;

/// <summary>Creates a new game with its first date, or (with ?gameId=) another date of an existing game.</summary>
[Authorize(Roles = "Admin")]
public class CreateModel(AppDbContext db) : PageModel
{
    [BindProperty] public SessionForm Input { get; set; } = new();
    [BindProperty(SupportsGet = true)] public Guid? GameId { get; set; }
    public Game? ExistingGame { get; private set; }

    public async Task<IActionResult> OnGetAsync(CancellationToken ct)
    {
        if (GameId is null) return Page();
        var latest = await db.GameSessions.Include(x => x.Game).AsNoTracking()
            .Where(x => x.GameId == GameId).OrderByDescending(x => x.StartsAt).FirstOrDefaultAsync(ct);
        ExistingGame = latest?.Game ?? await db.Games.AsNoTracking().FirstOrDefaultAsync(x => x.Id == GameId, ct);
        if (ExistingGame is null) return NotFound();
        if (latest is not null)
        {
            // Prefill venue, price and capacity from the latest date; only the date has to change.
            Input = SessionForm.From<SessionForm>(latest);
            Input.LocalStart = Input.LocalStart.AddDays(7);
        }
        return Page();
    }

    public async Task<IActionResult> OnPostAsync(CancellationToken ct)
    {
        if (GameId is not null)
        {
            ExistingGame = await db.Games.FirstOrDefaultAsync(x => x.Id == GameId, ct);
            if (ExistingGame is null) return NotFound();
            // The game card is not edited here: drop errors for fields that are not on the form.
            foreach (var key in ModelState.Keys.Where(k => k.StartsWith("Input.Title") || k.StartsWith("Input.Description")).ToList())
                ModelState.Remove(key);
        }
        var startsAt = Input.Validate(ModelState, requireTitle: ExistingGame is null);
        if (!ModelState.IsValid) return Page();

        var game = ExistingGame;
        if (game is null)
        {
            game = new Game();
            Input.ApplyToGame(game);
            db.Games.Add(game);
        }
        var session = new GameSession { Game = game };
        Input.ApplyToSession(session, startsAt);
        db.GameSessions.Add(session);
        await db.SaveChangesAsync(ct);
        TempData["Notice"] = ExistingGame is null ? "Игра создана." : "Новая дата добавлена.";
        return RedirectToPage("/Events/Details", new { id = session.Id });
    }
}
