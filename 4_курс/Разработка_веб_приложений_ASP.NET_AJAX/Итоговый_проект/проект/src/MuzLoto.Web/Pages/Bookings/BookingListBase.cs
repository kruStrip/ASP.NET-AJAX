using System.Security.Claims;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Mvc.RazorPages;
using Microsoft.EntityFrameworkCore;
using MuzLoto.Web.Data;
using MuzLoto.Web.Domain;
using MuzLoto.Web.Services;

namespace MuzLoto.Web.Pages.Bookings;

/// <summary>Shared plumbing for staff booking lists: scope by assignment, search, remaining seats, policy checks.</summary>
public abstract class BookingListBase(AppDbContext db, IAuthorizationService authorization) : PageModel
{
    protected AppDbContext Db => db;
    [BindProperty(SupportsGet = true)] public string? Q { get; set; }
    public List<Guid> AccessibleSessionIds { get; private set; } = new();
    public Dictionary<Guid, int> RemainingBySession { get; private set; } = new();
    protected string UserId => User.FindFirstValue(ClaimTypes.NameIdentifier)!;

    protected IQueryable<Booking> ScopedBookings()
    {
        var query = db.Bookings.Include(x => x.GameSession).ThenInclude(x => x.Game).Include(x => x.Messages).AsNoTracking();
        if (!User.IsInRole("Admin"))
        {
            var uid = UserId;
            query = query.Where(x => x.GameSession.Staff.Any(a => a.UserId == uid));
        }
        return BookingSearch.Apply(query, Q);
    }

    protected async Task LoadSessionsAsync(IEnumerable<Booking> shown, CancellationToken ct)
    {
        var scope = db.GameSessions.AsNoTracking().Where(s => s.CompletedAt == null && s.ArchivedAt == null);
        if (!User.IsInRole("Admin"))
        {
            var uid = UserId;
            scope = scope.Where(s => s.Staff.Any(a => a.UserId == uid));
        }
        AccessibleSessionIds = await scope.OrderBy(s => s.StartsAt).Select(s => s.Id).Take(250).ToListAsync(ct);
        var ids = shown.Select(x => x.GameSessionId).Distinct().ToArray();
        var sold = await db.Bookings.Where(x => ids.Contains(x.GameSessionId) && x.Status == BookingStatus.Confirmed)
            .GroupBy(x => x.GameSessionId).Select(g => new { g.Key, Sold = g.Sum(x => x.Quantity) })
            .ToDictionaryAsync(x => x.Key, x => x.Sold, ct);
        RemainingBySession = shown.Select(x => x.GameSession).DistinctBy(s => s.Id)
            .ToDictionary(s => s.Id, s => Math.Max(0, s.Capacity - sold.GetValueOrDefault(s.Id)));
    }

    /// <summary>Loads the booking and checks CanManageBooking; null result means the caller must return the given action.</summary>
    protected async Task<(Booking? Booking, IActionResult? Denied)> AuthorizeBookingAsync(Guid id, CancellationToken ct)
    {
        var booking = await db.Bookings.AsNoTracking().FirstOrDefaultAsync(x => x.Id == id, ct);
        if (booking is null) return (null, NotFound());
        return (await authorization.AuthorizeAsync(User, booking, BookingPolicies.CanManageBooking)).Succeeded
            ? (booking, null) : (null, Forbid());
    }

    protected async Task<IActionResult> RunAsync(Func<Task<string>> action)
    {
        try { return AjaxResult.Success(this, await action()); }
        catch (BusinessException ex) { return AjaxResult.Failure(this, ex); }
    }
}
