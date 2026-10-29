using System.Globalization;

namespace MuzLoto.Web.Services;

public static class ScheduleTime
{
    /// <summary>All games are in Vladivostok; new dates get this zone, existing ones keep theirs.</summary>
    public const string DefaultTimeZone = "Asia/Vladivostok";
    /// <summary>The one format staff read and type: 14.10.2026 19:00.</summary>
    public const string DateTimeFormat = "dd.MM.yyyy HH:mm";

    public static string Format(DateTimeOffset utcTime, string timeZoneId) =>
        ToLocal(utcTime, timeZoneId).ToString(DateTimeFormat, CultureInfo.InvariantCulture);

    public static string Format(DateTime localTime) => localTime.ToString(DateTimeFormat, CultureInfo.InvariantCulture);

    /// <summary>Parses "14.10.2026 19:00" (single-digit day, month or hour are accepted too).</summary>
    public static bool TryParseLocal(string? text, out DateTime localTime) =>
        DateTime.TryParseExact(text?.Trim(), ["dd.MM.yyyy HH:mm", "d.M.yyyy H:mm", "dd.MM.yyyy H:mm", "d.M.yyyy HH:mm"],
            CultureInfo.InvariantCulture, DateTimeStyles.None, out localTime);

    public static DateTimeOffset ToUtc(DateTime localTime, string timeZoneId)
    {
        var zone = TimeZoneInfo.FindSystemTimeZoneById(timeZoneId);
        var unspecified = DateTime.SpecifyKind(localTime, DateTimeKind.Unspecified);
        if (zone.IsInvalidTime(unspecified) || zone.IsAmbiguousTime(unspecified))
            throw new ArgumentException("Указанное местное время не существует или неоднозначно в выбранном часовом поясе.");
        return new DateTimeOffset(unspecified, zone.GetUtcOffset(unspecified)).ToUniversalTime();
    }

    public static DateTime ToLocal(DateTimeOffset utcTime, string timeZoneId) =>
        TimeZoneInfo.ConvertTime(utcTime, TimeZoneInfo.FindSystemTimeZoneById(timeZoneId)).DateTime;
}
