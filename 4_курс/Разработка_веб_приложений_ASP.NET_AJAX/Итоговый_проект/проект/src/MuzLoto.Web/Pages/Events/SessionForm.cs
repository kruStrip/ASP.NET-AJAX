using System.ComponentModel.DataAnnotations;
using Microsoft.AspNetCore.Mvc.ModelBinding;
using MuzLoto.Web.Domain;
using MuzLoto.Web.Services;

namespace MuzLoto.Web.Pages.Events;

/// <summary>Fields shared by the create and edit forms: the game card (shared by all its dates) and one session.</summary>
public class SessionForm
{
    [StringLength(200, MinimumLength = 3, ErrorMessage = "Название: от 3 до 200 символов.")]
    public string? Title { get; set; }
    /// <summary>One text field; each line is a paragraph (the game keeps up to five, the fifth takes the rest).</summary>
    [StringLength(5000, ErrorMessage = "Описание: не длиннее 5000 символов.")] public string? Description { get; set; }

    /// <summary>Local date and time of the game as typed: 14.10.2026 19:00 (the time zone is not edited in the UI).</summary>
    [Required(ErrorMessage = "Укажите дату и время.")]
    public string LocalStart { get; set; } = ScheduleTime.Format(DateTime.Today.AddDays(7).AddHours(19));
    [Required(ErrorMessage = "Укажите место проведения."), StringLength(250)] public string Venue { get; set; } = string.Empty;
    [StringLength(500)] public string? MapUrl { get; set; }
    [Range(1, 1440, ErrorMessage = "Продолжительность: от 1 до 1440 минут.")] public int DurationMinutes { get; set; } = 150;
    [Range(0, 21, ErrorMessage = "Возраст: от 0 до 21.")] public int AgeLimit { get; set; } = 18;
    [Range(0, 1000000, ErrorMessage = "Цена: от 0 до 1 000 000.")] public decimal TicketPrice { get; set; }
    [Range(1, 100000, ErrorMessage = "Мест: от 1 до 100 000.")] public int Capacity { get; set; } = 40;

    public string[] Descriptions => (Description ?? "").Split('\n', StringSplitOptions.TrimEntries | StringSplitOptions.RemoveEmptyEntries);

    private static string? Join(Game g) =>
        string.Join("\n", new[] { g.Description1, g.Description2, g.Description3, g.Description4, g.Description5 }
            .Where(x => !string.IsNullOrWhiteSpace(x)).Select(x => x!.Trim())) is { Length: > 0 } text ? text : null;

    /// <summary>Server-side checks that Data Annotations cannot express; returns the start time in UTC.</summary>
    /// <param name="timeZoneId">Zone the date is typed in: the default for new dates, the session's own when editing.</param>
    public DateTimeOffset Validate(ModelStateDictionary modelState, bool requireTitle, string timeZoneId)
    {
        if (requireTitle && string.IsNullOrWhiteSpace(Title))
            modelState.AddModelError("Input.Title", "Укажите название игры.");
        if (!string.IsNullOrWhiteSpace(MapUrl) && !LinkRules.IsHttpUrl(MapUrl))
            modelState.AddModelError("Input.MapUrl", "Ссылка должна начинаться с https:// (например, ссылка 2ГИС).");
        if (!ScheduleTime.TryParseLocal(LocalStart, out var local))
        {
            if (!string.IsNullOrWhiteSpace(LocalStart))
                modelState.AddModelError("Input.LocalStart", "Укажите дату и время в формате 14.10.2026 19:00.");
            return default;
        }
        try { return ScheduleTime.ToUtc(local, timeZoneId); }
        catch (Exception ex) when (ex is ArgumentException or TimeZoneNotFoundException or InvalidTimeZoneException)
        {
            modelState.AddModelError("Input.LocalStart", "Такого времени в этот день нет — проверьте дату и время.");
            return default;
        }
    }

    public void ApplyToGame(Game game)
    {
        game.Title = Title!.Trim();
        var lines = Descriptions;
        string? At(int i) => i < lines.Length ? lines[i] : null;
        var rest = lines.Length > 5 ? string.Join("\n", lines[4..]) : At(4); // more than five lines: the last column keeps them
        (game.Description1, game.Description2, game.Description3, game.Description4, game.Description5) = (At(0), At(1), At(2), At(3), rest);
    }

    public void ApplyToSession(GameSession s, DateTimeOffset startsAtUtc)
    {
        s.StartsAt = startsAtUtc;
        s.Venue = Venue.Trim();
        s.MapUrl = string.IsNullOrWhiteSpace(MapUrl) ? null : MapUrl.Trim();
        s.DurationMinutes = DurationMinutes;
        s.AgeLimit = AgeLimit;
        s.TicketPrice = TicketPrice;
        s.Capacity = Capacity;
    }

    public static T From<T>(GameSession s) where T : SessionForm, new() => new()
    {
        Title = s.Game.Title, Description = Join(s.Game),
        LocalStart = ScheduleTime.Format(s.StartsAt, s.TimeZoneId), Venue = s.Venue,
        MapUrl = s.MapUrl, DurationMinutes = s.DurationMinutes, AgeLimit = s.AgeLimit,
        TicketPrice = s.TicketPrice, Capacity = s.Capacity
    };
}
