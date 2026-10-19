using System.ComponentModel.DataAnnotations;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Mvc.RazorPages;
using MuzLoto.Web.Data;
using MuzLoto.Web.Domain;
using MuzLoto.Web.Services;

namespace MuzLoto.Web.Pages.Events;

[Authorize(Roles = "Admin")]
public class CreateModel(AppDbContext db) : PageModel
{
    [BindProperty] public InputModel Input { get; set; } = new();
    public sealed class InputModel
    {
        [Required, StringLength(200, MinimumLength = 3)] public string Title { get; set; } = string.Empty;
        [Required] public DateTime LocalStart { get; set; } = DateTime.Today.AddDays(7).AddHours(19);
        [Required] public string TimeZoneId { get; set; } = "Etc/UTC";
        [Required, StringLength(250)] public string Venue { get; set; } = string.Empty;
        [Range(0, 1000000)] public decimal TicketPrice { get; set; }
        [Range(1, 100000)] public int Capacity { get; set; } = 50;
    }
    public async Task<IActionResult> OnPostAsync(CancellationToken ct)
    {
        DateTimeOffset when = default;
        try { when = ScheduleTime.ToUtc(Input.LocalStart, Input.TimeZoneId); }
        catch (Exception ex) when (ex is ArgumentException or TimeZoneNotFoundException)
        { ModelState.AddModelError("Input.LocalStart", "Проверьте дату и часовой пояс."); }
        if (!ModelState.IsValid) return Page();
        var game = new Game { Title = Input.Title.Trim() };
        db.Games.Add(game);
        db.GameSessions.Add(new GameSession
        {
            Game = game, StartsAt = when, TimeZoneId = Input.TimeZoneId,
            Venue = Input.Venue.Trim(), TicketPrice = Input.TicketPrice,
            Capacity = Input.Capacity
        });
        await db.SaveChangesAsync(ct);
        TempData["Notice"] = "Игра создана.";
        return RedirectToPage("/Events/Index");
    }
}
