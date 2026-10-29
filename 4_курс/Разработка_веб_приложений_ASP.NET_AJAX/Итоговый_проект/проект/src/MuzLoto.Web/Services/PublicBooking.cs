using System.Globalization;
using System.Security.Cryptography;
using Microsoft.AspNetCore.DataProtection;
using Microsoft.EntityFrameworkCore;
using MuzLoto.Web.Data;
using MuzLoto.Web.Domain;

namespace MuzLoto.Web.Services;

/// <summary>A date a customer can book in the site form, with everything the game card shows.</summary>
public sealed record GameOffer(
    Guid Id, string Title, string Label, DateTime LocalStart, IReadOnlyList<string> Descriptions,
    string Venue, string? MapUrl, int DurationMinutes, int AgeLimit, decimal Price, int Remaining, int MaxQuantity,
    string? CoverUrl)
{
    public string Date => LocalStart.ToString("dd.MM.yyyy", CultureInfo.InvariantCulture);
    public string Time => LocalStart.ToString("HH:mm", CultureInfo.InvariantCulture);
}

/// <summary>Upcoming games open for sale, as the public booking form lists them.</summary>
public sealed class PublicSchedule(AppDbContext db, IConfiguration config)
{
    public async Task<List<GameOffer>> OpenAsync(CancellationToken ct = default)
    {
        var now = DateTimeOffset.UtcNow;
        var sessions = await db.GameSessions.AsNoTracking().Include(x => x.Game).Include(x => x.Images)
            .Where(x => x.CompletedAt == null && x.ArchivedAt == null && x.SalesOpen && x.StartsAt > now)
            .OrderBy(x => x.StartsAt).ToListAsync(ct);
        var ids = sessions.Select(x => x.Id).ToList();
        // Only paid bookings take seats; pending requests do not.
        var sold = await db.Bookings.Where(x => ids.Contains(x.GameSessionId) && x.Status == BookingStatus.Confirmed)
            .GroupBy(x => x.GameSessionId).Select(g => new { g.Key, Quantity = g.Sum(x => x.Quantity) })
            .ToDictionaryAsync(x => x.Key, x => x.Quantity, ct);
        var cap = config.GetValue<int?>("Booking:MaxTicketsPerBooking");

        var local = sessions.Select(s => (Session: s, Start: ScheduleTime.ToLocal(s.StartsAt, s.TimeZoneId))).ToList();
        // The list shows dates only ("05.11.2026"); the time is added when two games share a day.
        var sharedDays = local.GroupBy(x => x.Start.Date).Where(g => g.Count() > 1).Select(g => g.Key).ToHashSet();
        return local.Select(x =>
        {
            var s = x.Session; var g = s.Game;
            var remaining = Math.Max(0, s.Capacity - sold.GetValueOrDefault(s.Id));
            var label = x.Start.ToString(sharedDays.Contains(x.Start.Date) ? "dd.MM.yyyy HH:mm" : "dd.MM.yyyy", CultureInfo.InvariantCulture);
            var cover = s.Images.OrderBy(i => i.SortOrder).FirstOrDefault();
            return new GameOffer(s.Id, g.Title, label, x.Start,
                new[] { g.Description1, g.Description2, g.Description3, g.Description4, g.Description5 }
                    .Where(d => !string.IsNullOrWhiteSpace(d)).Select(d => d!.Trim()).ToList(),
                s.Venue, LinkRules.IsHttpUrl(s.MapUrl) ? s.MapUrl : null, s.DurationMinutes, s.AgeLimit, s.TicketPrice,
                remaining, Math.Min(remaining, cap ?? remaining), cover is null ? null : CoverImages.Url(cover));
        }).ToList();
    }

    /// <summary>120 → «2 часа», 90 → «1 час 30 мин».</summary>
    public static string Duration(int minutes)
    {
        var (h, m) = (minutes / 60, minutes % 60);
        var hours = h == 0 ? "" : $"{h} " + Plural(h, "час", "часа", "часов");
        return m == 0 ? hours : (hours + $" {m} мин").Trim();
    }

    private static string Plural(int n, string one, string few, string many) =>
        (n % 100) is >= 11 and <= 14 ? many : (n % 10) switch { 1 => one, >= 2 and <= 4 => few, _ => many };
}

/// <summary>
/// Link to a booking's payment screen. The booking ID is never exposed bare: the link is an encrypted,
/// time-limited token, so nobody can browse other customers' bookings by guessing IDs.
/// </summary>
public sealed class PaymentLinks(IDataProtectionProvider provider)
{
    private static readonly TimeSpan Lifetime = TimeSpan.FromDays(60);
    private ITimeLimitedDataProtector Protector =>
        provider.CreateProtector("MuzLoto.PaymentPage.v1").ToTimeLimitedDataProtector();

    public string Create(Guid bookingId) => Protector.Protect(bookingId.ToString("N"), Lifetime);

    public Guid? Read(string? token)
    {
        if (string.IsNullOrEmpty(token)) return null;
        try { return Guid.TryParseExact(Protector.Unprotect(token), "N", out var id) ? id : null; }
        catch (CryptographicException) { return null; }
    }
}
