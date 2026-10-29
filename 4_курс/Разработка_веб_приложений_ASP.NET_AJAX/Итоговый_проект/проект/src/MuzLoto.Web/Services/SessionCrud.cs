using System.Data;
using System.Security.Claims;
using Microsoft.EntityFrameworkCore;
using MuzLoto.Web.Data;
using MuzLoto.Web.Domain;

namespace MuzLoto.Web.Services;

public sealed record SessionWrite(
    Guid GameId, DateTimeOffset StartsAt, string TimeZoneId, string Venue,
    int Capacity, decimal TicketPrice, int DurationMinutes, int AgeLimit,
    bool SalesOpen, string? MapUrl = null, bool SetMapUrl = false);

public sealed record SessionSnapshot(
    Guid Id, Guid GameId, string GameTitle, DateTimeOffset StartsAt,
    string TimeZoneId, string Venue, int Capacity, int Reserved,
    decimal TicketPrice, int DurationMinutes, int AgeLimit, bool SalesOpen,
    bool Completed, string? MapUrl);

public sealed class SessionCrud(AppDbContext db)
{
    public IQueryable<GameSession> AccessibleSessions(ClaimsPrincipal user)
    {
        var query = db.GameSessions.AsNoTracking().Include(x => x.Game).AsQueryable();
        if (!user.IsInRole("Admin"))
        {
            var uid = user.FindFirstValue(ClaimTypes.NameIdentifier);
            query = query.Where(x => x.Staff.Any(a => a.UserId == uid));
        }
        return query;
    }

    public async Task<SessionSnapshot?> GetAsync(ClaimsPrincipal user, Guid id, CancellationToken ct)
    {
        var s = await AccessibleSessions(user).SingleOrDefaultAsync(x => x.Id == id, ct);
        return s is null ? null : await DescribeAsync(s, ct);
    }

    public async Task<IReadOnlyList<SessionSnapshot>> ListAsync(
        ClaimsPrincipal user, int page, int pageSize, string? search, CancellationToken ct)
    {
        var query = AccessibleSessions(user);
        if (!string.IsNullOrWhiteSpace(search))
        {
            var term = search.Trim();
            query = query.Where(x => EF.Functions.ILike(x.Game.Title, "%" + term + "%") ||
                                     EF.Functions.ILike(x.Venue, "%" + term + "%"));
        }
        var sessions = await query.OrderBy(x => x.StartsAt).ThenBy(x => x.Id)
            .Skip((page - 1) * pageSize).Take(pageSize).ToListAsync(ct);
        var ids = sessions.Select(x => x.Id).ToArray();
        var counts = await db.Bookings.AsNoTracking()
            .Where(x => ids.Contains(x.GameSessionId) && x.Status == BookingStatus.Confirmed)
            .GroupBy(x => x.GameSessionId)
            .Select(x => new { Id = x.Key, Count = x.Sum(v => v.Quantity) })
            .ToDictionaryAsync(x => x.Id, x => x.Count, ct);
        return sessions.Select(x => Map(x, counts.GetValueOrDefault(x.Id))).ToList();
    }

    public async Task<SessionSnapshot> CreateAsync(SessionWrite input, CancellationToken ct)
    {
        Validate(input);
        var game = await db.Games.FindAsync(new object[] { input.GameId }, ct)
            ?? throw new BusinessException("Игра не найдена.", 404);
        var session = new GameSession { GameId = game.Id };
        Update(session, input);
        db.GameSessions.Add(session);
        await db.SaveChangesAsync(ct);
        session.Game = game;
        return Map(session, 0);
    }

    public async Task<SessionSnapshot> UpdateAsync(Guid id, SessionWrite input, CancellationToken ct)
    {
        Validate(input);
        await using var tx = await db.Database.BeginTransactionAsync(IsolationLevel.Serializable, ct);
        var session = await db.GameSessions.Include(x => x.Game).SingleOrDefaultAsync(x => x.Id == id, ct)
            ?? throw new BusinessException("Сеанс не найден.", 404);
        if (session.CompletedAt is not null) throw new BusinessException("Завершённый сеанс нельзя менять.", 409);
        if (session.GameId != input.GameId && !await db.Games.AnyAsync(x => x.Id == input.GameId, ct))
            throw new BusinessException("Игра не найдена.", 404);
        var reserved = await db.Bookings.Where(x => x.GameSessionId == id && x.Status == BookingStatus.Confirmed)
            .SumAsync(x => (int?)x.Quantity, ct) ?? 0;
        if (input.Capacity < reserved) throw new BusinessException("Вместимость меньше оплаченного количества мест.", 409);
        Update(session, input);
        await db.SaveChangesAsync(ct);
        await tx.CommitAsync(ct);
        // GameId may have changed: point the navigation at the game it now references.
        session.Game = await db.Games.SingleAsync(x => x.Id == session.GameId, ct);
        return Map(session, reserved);
    }

    public async Task DeleteAsync(Guid id, CancellationToken ct)
    {
        await using var tx = await db.Database.BeginTransactionAsync(IsolationLevel.Serializable, ct);
        var session = await db.GameSessions.SingleOrDefaultAsync(x => x.Id == id, ct)
            ?? throw new BusinessException("Сеанс не найден.", 404);
        if (await db.Bookings.AnyAsync(x => x.GameSessionId == id, ct))
            throw new BusinessException("Сеанс с заявками нельзя удалять: закройте продажи или завершите его.", 409);
        db.GameSessions.Remove(session);
        await db.SaveChangesAsync(ct);
        await tx.CommitAsync(ct);
    }

    public async Task<SessionSnapshot> DescribeAsync(GameSession s, CancellationToken ct)
    {
        var reserved = await db.Bookings.AsNoTracking()
            .Where(x => x.GameSessionId == s.Id && x.Status == BookingStatus.Confirmed)
            .SumAsync(x => (int?)x.Quantity, ct) ?? 0;
        return Map(s, reserved);
    }

    private static SessionSnapshot Map(GameSession s, int reserved) => new(
        s.Id, s.GameId, s.Game.Title, s.StartsAt, s.TimeZoneId, s.Venue,
        s.Capacity, reserved, s.TicketPrice, s.DurationMinutes, s.AgeLimit, s.SalesOpen,
        s.CompletedAt.HasValue, s.MapUrl);

    private static void Update(GameSession s, SessionWrite input)
    {
        s.GameId = input.GameId;
        s.StartsAt = input.StartsAt.ToUniversalTime();
        s.TimeZoneId = input.TimeZoneId.Trim();
        s.Venue = input.Venue.Trim();
        s.Capacity = input.Capacity;
        s.TicketPrice = input.TicketPrice;
        s.DurationMinutes = input.DurationMinutes;
        s.AgeLimit = input.AgeLimit;
        s.SalesOpen = input.SalesOpen;
        // v1 has no mapUrl field: its PUT keeps the stored link instead of erasing it.
        if (input.SetMapUrl) s.MapUrl = string.IsNullOrWhiteSpace(input.MapUrl) ? null : input.MapUrl.Trim();
    }
    private static void Validate(SessionWrite i)
    {
        if (i.GameId == Guid.Empty || string.IsNullOrWhiteSpace(i.TimeZoneId) || i.TimeZoneId.Length > 100 ||
            string.IsNullOrWhiteSpace(i.Venue) || i.Venue.Length > 250 ||
            i.Capacity is < 1 or > 100_000 || i.TicketPrice is < 0 or > 100_000_000 ||
            i.DurationMinutes is < 1 or > 1_440 || i.AgeLimit is < 0 or > 21 ||
            i.StartsAt == default)
            throw new BusinessException("Некорректные параметры сеанса.", 400);
        if (!string.IsNullOrWhiteSpace(i.MapUrl) && (i.MapUrl.Length > 500 ||
            !Uri.TryCreate(i.MapUrl.Trim(), UriKind.Absolute, out var map) || map.Scheme is not ("http" or "https")))
            throw new BusinessException("Ссылка на карту должна быть адресом http(s).", 400);
        try { _ = TimeZoneInfo.FindSystemTimeZoneById(i.TimeZoneId); }
        catch (TimeZoneNotFoundException) { throw new BusinessException("Неизвестный часовой пояс.", 400); }
        catch (InvalidTimeZoneException) { throw new BusinessException("Некорректный часовой пояс.", 400); }
    }
}
