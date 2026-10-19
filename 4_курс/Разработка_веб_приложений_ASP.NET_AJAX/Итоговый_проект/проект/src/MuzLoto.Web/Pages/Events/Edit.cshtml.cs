using System.ComponentModel.DataAnnotations;
using System.Data;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Mvc.RazorPages;
using Microsoft.EntityFrameworkCore;
using MuzLoto.Web.Data;
using MuzLoto.Web.Domain;
using MuzLoto.Web.Services;
using Npgsql;

namespace MuzLoto.Web.Pages.Events;

[Authorize(Roles = "Admin")]
public class EditModel(AppDbContext db) : PageModel
{
    [BindProperty] public EditInput Input { get; set; } = new();
    public sealed class EditInput
    {
        [Required, StringLength(200, MinimumLength = 3)] public string Title { get; set; } = string.Empty;
        [Required] public DateTime LocalStart { get; set; }
        [Required] public string TimeZoneId { get; set; } = "Etc/UTC";
        [Required, StringLength(250)] public string Venue { get; set; } = string.Empty;
        [Range(0, 1000000)] public decimal TicketPrice { get; set; }
        [Range(1, 100000)] public int Capacity { get; set; }
        public bool SalesOpen { get; set; }
    }
    public Guid SessionId { get; private set; }
    public async Task<IActionResult> OnGetAsync(Guid id, CancellationToken ct)
    {
        var s = await db.GameSessions.Include(x => x.Game).AsNoTracking().FirstOrDefaultAsync(x => x.Id == id, ct);
        if (s is null) return NotFound();
        SessionId = id;
        Input = new EditInput
        {
            Title = s.Game.Title, LocalStart = ScheduleTime.ToLocal(s.StartsAt, s.TimeZoneId),
            TimeZoneId = s.TimeZoneId, Venue = s.Venue,
            TicketPrice = s.TicketPrice, Capacity = s.Capacity, SalesOpen = s.SalesOpen
        };
        return Page();
    }
    public async Task<IActionResult> OnPostAsync(Guid id, CancellationToken ct)
    {
        SessionId = id;
        DateTimeOffset start = default;
        try { start = ScheduleTime.ToUtc(Input.LocalStart, Input.TimeZoneId); }
        catch (Exception ex) when (ex is ArgumentException or TimeZoneNotFoundException)
        { ModelState.AddModelError("Input.LocalStart", "Неверный часовой пояс или неоднозначное время."); }
        if (!ModelState.IsValid) return Page();
        try
        {
            await using var tx = await db.Database.BeginTransactionAsync(IsolationLevel.Serializable, ct);
            var s = await db.GameSessions.Include(x => x.Game).FirstOrDefaultAsync(x => x.Id == id, ct);
            if (s is null) return NotFound();
            if (s.CompletedAt.HasValue)
            {
                ModelState.AddModelError(string.Empty, "Завершённую игру нельзя редактировать в этой версии.");
                return Page();
            }
            var reserved = await db.Bookings.Where(x => x.GameSessionId == id && x.Status == BookingStatus.Confirmed)
                .SumAsync(x => (int?)x.Quantity, ct) ?? 0;
            if (Input.Capacity < reserved)
            {
                ModelState.AddModelError("Input.Capacity", $"Уже подтверждено билетов: {reserved}.");
                return Page();
            }
            s.Game.Title = Input.Title.Trim();
            s.StartsAt = start;
            s.TimeZoneId = Input.TimeZoneId;
            s.Venue = Input.Venue.Trim();
            s.TicketPrice = Input.TicketPrice;
            s.Capacity = Input.Capacity;
            s.SalesOpen = Input.SalesOpen;
            await db.SaveChangesAsync(ct);
            await tx.CommitAsync(ct);
        }
        catch (PostgresException ex) when (ex.SqlState == PostgresErrorCodes.SerializationFailure)
        {
            ModelState.AddModelError(string.Empty, "Данные изменились одновременно. Повторите сохранение.");
            return Page();
        }
        TempData["Notice"] = "Данные игры обновлены.";
        return RedirectToPage("/Events/Index");
    }
}
