using MuzLoto.Web.Services;
using Xunit;

namespace MuzLoto.Tests;

public class ScheduleTimeTests
{
    [Fact]
    public void ConvertsExplicitUtcLocalTime()
    {
        var value = ScheduleTime.ToUtc(new DateTime(2026, 10, 20, 19, 0, 0), "Etc/UTC");
        Assert.Equal(TimeSpan.Zero, value.Offset);
        Assert.Equal(19, value.Hour);
    }

    [Fact]
    public void RejectsAmbiguousLocalTime()
    {
        Assert.Throws<ArgumentException>(() => ScheduleTime.ToUtc(
            new DateTime(2026, 10, 25, 2, 30, 0), "Europe/Berlin"));
    }
}
