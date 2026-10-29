using System.ComponentModel.DataAnnotations;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Mvc.RazorPages;
using Microsoft.EntityFrameworkCore;
using MuzLoto.Web.Data;
using MuzLoto.Web.Domain;
using MuzLoto.Web.Services;

namespace MuzLoto.Web.Pages.Bookings;

/// <summary>Resource-based authorization: the booking is loaded first, then checked against CanEditBooking.</summary>
[Authorize]
public class EditModel(AppDbContext db, IAuthorizationService authorization, BookingWorkflow workflow) : PageModel
{
    [BindProperty] public EditInput Input { get; set; } = new();
    public sealed class EditInput
    {
        [Required(ErrorMessage = "Укажите название команды."), StringLength(150)] public string TeamName { get; set; } = string.Empty;
        [Required(ErrorMessage = "Укажите имя."), StringLength(150)] public string CustomerName { get; set; } = string.Empty;
        [StringLength(100)] public string? Telegram { get; set; }
        [Range(1, 100000, ErrorMessage = "Укажите количество билетов.")] public int Quantity { get; set; }
    }
    public string GameTitle { get; private set; } = string.Empty;
    public string StartsLocal { get; private set; } = string.Empty;
    public int Remaining { get; private set; }

    public async Task<IActionResult> OnGetAsync(Guid id, CancellationToken ct)
    {
        var booking = await LoadAsync(id, ct);
        if (booking is null) return NotFound();
        if (!await CanEditAsync(booking)) return Forbid();
        Input = new EditInput
        {
            TeamName = booking.TeamName, CustomerName = booking.CustomerName,
            Telegram = booking.Telegram, Quantity = booking.Quantity
        };
        return Page();
    }

    public async Task<IActionResult> OnPostAsync(Guid id, CancellationToken ct)
    {
        var booking = await LoadAsync(id, ct);
        if (booking is null) return NotFound();
        if (!await CanEditAsync(booking)) return Forbid();
        var telegram = ContactRules.NormalizeTelegram(Input.Telegram);
        if (!string.IsNullOrWhiteSpace(Input.Telegram) && telegram is null)
            ModelState.AddModelError("Input.Telegram", "Укажите ник в формате @username (не почту и не номер).");
        if (Input.Quantity > Remaining)
            ModelState.AddModelError("Input.Quantity", $"Свободно только {Remaining} бил.");
        if (!ModelState.IsValid) return Page();
        try
        {
            await workflow.UpdatePendingAsync(id, Input.TeamName, Input.CustomerName, telegram, Input.Quantity, ct);
        }
        catch (BusinessException ex)
        {
            ModelState.AddModelError(string.Empty, ex.Message);
            return Page();
        }
        TempData["Notice"] = "Заявка обновлена.";
        return User.IsInRole("Admin") || User.IsInRole("Manager")
            ? RedirectToPage("/Bookings/Pending") : RedirectToPage("/Bookings/Mine");
    }

    private async Task<bool> CanEditAsync(Booking booking) =>
        (await authorization.AuthorizeAsync(User, booking, BookingPolicies.CanEditBooking)).Succeeded;

    private async Task<Booking?> LoadAsync(Guid id, CancellationToken ct)
    {
        var booking = await db.Bookings.Include(x => x.GameSession).ThenInclude(x => x.Game).AsNoTracking()
            .FirstOrDefaultAsync(x => x.Id == id, ct);
        if (booking is null) return null;
        var reserved = await db.Bookings
            .Where(x => x.GameSessionId == booking.GameSessionId && x.Status == BookingStatus.Confirmed)
            .SumAsync(x => (int?)x.Quantity, ct) ?? 0;
        Remaining = Math.Max(0, booking.GameSession.Capacity - reserved);
        GameTitle = booking.GameSession.Game.Title;
        StartsLocal = ScheduleTime.ToLocal(booking.GameSession.StartsAt, booking.GameSession.TimeZoneId).ToString("dd.MM.yyyy HH:mm");
        return booking;
    }
}
