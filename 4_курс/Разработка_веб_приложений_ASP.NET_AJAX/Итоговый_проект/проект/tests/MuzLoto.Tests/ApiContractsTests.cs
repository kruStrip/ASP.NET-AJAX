using Microsoft.Extensions.Configuration;
using MuzLoto.Web.Controllers;
using MuzLoto.Web.Services;
using Xunit;

namespace MuzLoto.Tests;

public class ApiContractsTests
{
    [Fact]
    public void V2Write_MapsNestedLocationAndSeats()
    {
        var request = new EventsV2Controller.WriteDto
        {
            GameId = Guid.NewGuid(), StartsAt = new DateTimeOffset(2026, 11, 12, 19, 0, 0, TimeSpan.FromHours(3)),
            Location = new() { Name = "Зал", TimeZone = "Europe/Moscow" },
            Seats = new() { Total = 80 }, TicketPrice = 1200
        };
        var write = request.ToWrite();
        Assert.Equal(80, write.Capacity);
        Assert.Equal("Europe/Moscow", write.TimeZoneId);
        Assert.Equal("Зал", write.Venue);
    }

    [Fact]
    public void MissingSigningKey_IsRejected()
    {
        var cfg = new ConfigurationBuilder().AddInMemoryCollection().Build();
        Assert.Throws<InvalidOperationException>(() => JwtTokenService.RequireSigningKey(cfg));
    }

    [Fact]
    public void ShortSigningKey_IsRejected()
    {
        var cfg = new ConfigurationBuilder().AddInMemoryCollection(new Dictionary<string, string?>
        { ["Jwt:Key"] = "unsafe" }).Build();
        Assert.Throws<InvalidOperationException>(() => JwtTokenService.RequireSigningKey(cfg));
    }
}
