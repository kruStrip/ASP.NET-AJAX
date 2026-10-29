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
    [StringLength(1000)] public string? Description1 { get; set; }
    [StringLength(1000)] public string? Description2 { get; set; }
    [StringLength(1000)] public string? Description3 { get; set; }
    [StringLength(1000)] public string? Description4 { get; set; }
    [StringLength(1000)] public string? Description5 { get; set; }

    [Required(ErrorMessage = "Укажите дату и время.")] public DateTime LocalStart { get; set; } = DateTime.Today.AddDays(7).AddHours(19);
    [Required(ErrorMessage = "Укажите часовой пояс.")] public string TimeZoneId { get; set; } = "Asia/Vladivostok";
    [Required(ErrorMessage = "Укажите место проведения."), StringLength(250)] public string Venue { get; set; } = string.Empty;
    [StringLength(500)] public string? MapUrl { get; set; }
    [Range(1, 1440, ErrorMessage = "Продолжительность: от 1 до 1440 минут.")] public int DurationMinutes { get; set; } = 150;
    [Range(0, 21, ErrorMessage = "Возраст: от 0 до 21.")] public int AgeLimit { get; set; } = 18;
    [Range(0, 1000000, ErrorMessage = "Цена: от 0 до 1 000 000.")] public decimal TicketPrice { get; set; }
    [Range(1, 100000, ErrorMessage = "Мест: от 1 до 100 000.")] public int Capacity { get; set; } = 40;

    public string[] Descriptions => new[] { Description1, Description2, Description3, Description4, Description5 }
        .Where(x => !string.IsNullOrWhiteSpace(x)).Select(x => x!.Trim()).ToArray();

    /// <summary>Server-side checks that Data Annotations cannot express; returns the start time in UTC.</summary>
    public DateTimeOffset Validate(ModelStateDictionary modelState, bool requireTitle)
    {
        if (requireTitle && string.IsNullOrWhiteSpace(Title))
            modelState.AddModelError("Input.Title", "Укажите название игры.");
        if (!string.IsNullOrWhiteSpace(MapUrl) && !LinkRules.IsHttpUrl(MapUrl))
            modelState.AddModelError("Input.MapUrl", "Ссылка должна начинаться с https:// (например, ссылка 2ГИС).");
        try { return ScheduleTime.ToUtc(LocalStart, TimeZoneId); }
        catch (Exception ex) when (ex is ArgumentException or TimeZoneNotFoundException or InvalidTimeZoneException)
        {
            modelState.AddModelError("Input.LocalStart", "Проверьте дату и часовой пояс (IANA, например Asia/Vladivostok).");
            return default;
        }
    }

    public void ApplyToGame(Game game)
    {
        game.Title = Title!.Trim();
        var d = new[] { Description1, Description2, Description3, Description4, Description5 }
            .Select(x => string.IsNullOrWhiteSpace(x) ? null : x.Trim()).ToArray();
        (game.Description1, game.Description2, game.Description3, game.Description4, game.Description5) = (d[0], d[1], d[2], d[3], d[4]);
    }

    public void ApplyToSession(GameSession s, DateTimeOffset startsAtUtc)
    {
        s.StartsAt = startsAtUtc;
        s.TimeZoneId = TimeZoneId.Trim();
        s.Venue = Venue.Trim();
        s.MapUrl = string.IsNullOrWhiteSpace(MapUrl) ? null : MapUrl.Trim();
        s.DurationMinutes = DurationMinutes;
        s.AgeLimit = AgeLimit;
        s.TicketPrice = TicketPrice;
        s.Capacity = Capacity;
    }

    public static T From<T>(GameSession s) where T : SessionForm, new() => new()
    {
        Title = s.Game.Title, Description1 = s.Game.Description1, Description2 = s.Game.Description2,
        Description3 = s.Game.Description3, Description4 = s.Game.Description4, Description5 = s.Game.Description5,
        LocalStart = ScheduleTime.ToLocal(s.StartsAt, s.TimeZoneId), TimeZoneId = s.TimeZoneId, Venue = s.Venue,
        MapUrl = s.MapUrl, DurationMinutes = s.DurationMinutes, AgeLimit = s.AgeLimit,
        TicketPrice = s.TicketPrice, Capacity = s.Capacity
    };
}
