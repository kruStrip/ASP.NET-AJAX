using System.Globalization;
using System.Security.Cryptography;
using System.Text;
using Microsoft.EntityFrameworkCore;
using MuzLoto.Web.Data;

namespace MuzLoto.Web.Services;

/// <summary>
/// Turns a FormDesigner webhook (flat parameters named in the webhook settings) into a booking request.
/// The form knows the game by its date, time and title, not by our session ID, so the session is resolved here.
/// </summary>
public sealed class FormDesignerWebhook(AppDbContext db, BookingWorkflow workflow, IConfiguration config, ILogger<FormDesignerWebhook> logger)
{
    /// <summary>Parameter names to enter in FormDesigner → WebHooks → «Параметры запроса из формы».</summary>
    public static class Fields
    {
        public const string Key = "key", SubmissionId = "submission_id", GameId = "game_id", GameDate = "game_date",
            GameTime = "game_time", GameName = "game_name", Quantity = "quantity", Name = "name", Phone = "phone",
            Telegram = "telegram", Team = "team", Total = "total", SentAt = "sent_at";
    }

    public sealed record Result(int StatusCode, string Message, Guid? BookingId = null, bool Duplicate = false);

    public bool IsAuthorized(string? suppliedKey)
    {
        var secret = config["Integration:Secret"];
        if (string.IsNullOrWhiteSpace(secret) || string.IsNullOrEmpty(suppliedKey)) return false;
        return CryptographicOperations.FixedTimeEquals(
            SHA256.HashData(Encoding.UTF8.GetBytes(suppliedKey)), SHA256.HashData(Encoding.UTF8.GetBytes(secret)));
    }

    public async Task<Result> ReceiveAsync(IReadOnlyDictionary<string, string?> p, CancellationToken ct)
    {
        string? Get(string name) => p.TryGetValue(name, out var v) && !string.IsNullOrWhiteSpace(v) ? v.Trim() : null;
        // Field names only: values contain customers' personal data and are never logged.
        logger.LogInformation("FormDesigner webhook received with fields: {Fields}", string.Join(", ", p.Keys.Where(k => k != Fields.Key).Order()));

        var name = Get(Fields.Name); var phone = Get(Fields.Phone); var team = Get(Fields.Team);
        var quantityText = Get(Fields.Quantity);
        var missing = new[] { (Fields.Name, name), (Fields.Phone, phone), (Fields.Team, team), (Fields.Quantity, quantityText) }
            .Where(x => x.Item2 is null).Select(x => x.Item1).ToList();
        if (Get(Fields.GameId) is null && Get(Fields.GameDate) is null) missing.Add(Fields.GameDate);
        if (missing.Count > 0) return new(400, "Не заполнены параметры: " + string.Join(", ", missing));
        if (!int.TryParse(new string(quantityText!.Where(char.IsDigit).ToArray()), out var quantity) || quantity < 1)
            return new(400, $"Некорректное количество билетов: «{quantityText}».");

        var session = await ResolveSessionAsync(Get(Fields.GameId), Get(Fields.GameDate), Get(Fields.GameTime), Get(Fields.GameName), ct);
        if (session.Error is not null) return session.Error;

        var total = ParseMoney(Get(Fields.Total));
        if (total is not null && total != quantity * session.Price)
            logger.LogWarning("FormDesigner total {Total} differs from {Quantity} × {Price}; the booking keeps the price from the database",
                total, quantity, session.Price);

        // FormDesigner may retry a delivery; the same submission must not create a second booking.
        var externalId = Get(Fields.SubmissionId) is { } sid ? "fd-" + sid : "fd-hash-" + FallbackKey(p, Get(Fields.SentAt));
        try
        {
            var (bookingId, duplicate) = await workflow.ReceiveAsync(new IncomingBooking(
                externalId[..Math.Min(externalId.Length, 250)], session.Id, Clip(team!, 150), Clip(name!, 150),
                ContactRules.NormalizePhone(phone) ?? Clip(phone!, 40),
                ContactRules.NormalizeTelegram(Get(Fields.Telegram)) ?? (Get(Fields.Telegram) is { } t ? Clip(t, 100) : null),
                quantity), ct);
            return new(duplicate ? 200 : 201, duplicate ? "Повторная доставка: заявка уже принята." : "Заявка принята.", bookingId, duplicate);
        }
        catch (BusinessException ex) { return new(ex.StatusCode, ex.Message); }
    }

    private sealed record Resolved(Guid Id, decimal Price, Result? Error);

    /// <summary>Finds the date the customer chose: by our ID if the form sends it, otherwise by local date (+ time, + title).</summary>
    private async Task<Resolved> ResolveSessionAsync(string? gameId, string? date, string? time, string? title, CancellationToken ct)
    {
        var open = db.GameSessions.AsNoTracking().Include(x => x.Game).Where(x => x.CompletedAt == null && x.ArchivedAt == null);
        if (Guid.TryParse(gameId, out var id))
        {
            var byId = await open.Where(x => x.Id == id).Select(x => new { x.Id, x.TicketPrice }).FirstOrDefaultAsync(ct);
            return byId is null ? new(default, 0, new(404, "Игра с таким ID не найдена или уже завершена."))
                                : new(byId.Id, byId.TicketPrice, null);
        }
        if (!DateOnly.TryParseExact(date, ["dd.MM.yyyy", "d.M.yyyy", "yyyy-MM-dd", "dd.MM.yy"], CultureInfo.InvariantCulture, DateTimeStyles.None, out var day))
            return new(default, 0, new(400, $"Некорректная дата игры: «{date}». Ожидается дд.мм.гггг."));

        // Narrow by a ±1 day UTC window, then compare in each session's own time zone.
        var from = new DateTimeOffset(day.ToDateTime(TimeOnly.MinValue), TimeSpan.Zero).AddDays(-1);
        var candidates = (await open.Where(x => x.StartsAt >= from && x.StartsAt < from.AddDays(3)).ToListAsync(ct))
            .Where(x => DateOnly.FromDateTime(ScheduleTime.ToLocal(x.StartsAt, x.TimeZoneId)) == day).ToList();
        if (TimeOnly.TryParse(time, CultureInfo.InvariantCulture, out var at))
            candidates = candidates.Where(x => TimeOnly.FromDateTime(ScheduleTime.ToLocal(x.StartsAt, x.TimeZoneId)) is var local
                && local.Hour == at.Hour && local.Minute == at.Minute).ToList();
        if (candidates.Count > 1 && title is not null)
        {
            // The title only narrows the choice: a spelling difference must not turn "which game?" into "no game".
            var byTitle = candidates.Where(x => x.Game.Title.Contains(title, StringComparison.OrdinalIgnoreCase)
                || title.Contains(x.Game.Title, StringComparison.OrdinalIgnoreCase)).ToList();
            if (byTitle.Count > 0) candidates = byTitle;
        }
        return candidates.Count switch
        {
            1 => new(candidates[0].Id, candidates[0].TicketPrice, null),
            0 => new(default, 0, new(404, $"На {date}{(time is null ? "" : " " + time)} нет открытой игры. Проверьте расписание.")),
            _ => new(default, 0, new(409, $"На {date} несколько игр — передайте время (game_time) или название (game_name)."))
        };
    }

    /// <summary>Without a submission ID, a retry is recognised by identical content within the same hour.</summary>
    private static string FallbackKey(IReadOnlyDictionary<string, string?> p, string? sentAt)
    {
        var content = string.Join("|", p.Where(x => x.Key is not (Fields.Key or Fields.SentAt)).OrderBy(x => x.Key, StringComparer.Ordinal)
            .Select(x => x.Key + "=" + x.Value?.Trim()));
        var window = sentAt ?? DateTimeOffset.UtcNow.ToString("yyyyMMddHH", CultureInfo.InvariantCulture);
        return Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(content + "|" + window)))[..40];
    }

    private static decimal? ParseMoney(string? text)
    {
        var digits = new string((text ?? "").Where(c => char.IsDigit(c) || c is ',' or '.').ToArray()).Replace(',', '.');
        return decimal.TryParse(digits, NumberStyles.Number, CultureInfo.InvariantCulture, out var value) ? value : null;
    }

    private static string Clip(string value, int max) => value.Length <= max ? value : value[..max];
}
