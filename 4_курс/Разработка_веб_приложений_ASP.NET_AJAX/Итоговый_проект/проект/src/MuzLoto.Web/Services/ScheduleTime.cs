namespace MuzLoto.Web.Services;

public static class ScheduleTime
{
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
