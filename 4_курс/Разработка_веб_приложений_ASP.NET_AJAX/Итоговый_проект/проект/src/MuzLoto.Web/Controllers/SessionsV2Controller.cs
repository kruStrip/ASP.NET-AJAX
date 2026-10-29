using System.ComponentModel.DataAnnotations;
using Microsoft.AspNetCore.Authentication.JwtBearer;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using MuzLoto.Web.Services;

namespace MuzLoto.Web.Controllers;

[ApiController]
[IgnoreAntiforgeryToken]
[Authorize(AuthenticationSchemes = JwtBearerDefaults.AuthenticationScheme, Roles = "Admin,Manager")]
[Route("api/v2/sessions")]
public sealed class SessionsV2Controller(SessionCrud sessions) : SessionApiBase
{
    // v2 is a genuinely different DTO contract: nested location and seat plan,
    // plus an availability summary rather than independent capacity/reserved fields.
    public sealed class LocationDto
    {
        [Required, StringLength(250)] public string Name { get; set; } = string.Empty;
        [Required, StringLength(100)] public string TimeZone { get; set; } = "Europe/Moscow";
    }
    public sealed class SeatsDto
    {
        [Range(1, 100000)] public int Total { get; set; }
    }
    public sealed class WriteDto
    {
        [Required] public Guid GameId { get; set; }
        [Required] public DateTimeOffset StartsAt { get; set; }
        [Required] public LocationDto Location { get; set; } = new();
        [Required] public SeatsDto Seats { get; set; } = new();
        [Range(typeof(decimal), "0", "100000000")] public decimal TicketPrice { get; set; }
        [Range(1, 1440)] public int DurationMinutes { get; set; } = 120;
        [Range(0, 21)] public int AgeLimit { get; set; } = 12;
        public bool SalesOpen { get; set; } = true;
        public SessionWrite ToWrite() => new(GameId, StartsAt, Location.TimeZone, Location.Name,
            Seats.Total, TicketPrice, DurationMinutes, AgeLimit, SalesOpen);
    }
    public sealed record AvailabilityDto(int Total, int Sold, int Remaining);
    public sealed record ReadDto(Guid Id, Guid GameId, string GameTitle, DateTimeOffset StartsAt,
        LocationDto Location, AvailabilityDto Availability, decimal TicketPrice,
        string Status, int DurationMinutes, int AgeLimit);
    private static ReadDto Map(SessionSnapshot s) => new(
        s.Id, s.GameId, s.GameTitle, s.StartsAt,
        new LocationDto { Name = s.Venue, TimeZone = s.TimeZoneId },
        new AvailabilityDto(s.Capacity, s.Reserved, Math.Max(0, s.Capacity - s.Reserved)),
        s.TicketPrice, s.Completed ? "completed" : s.SalesOpen ? "open" : "closed",
        s.DurationMinutes, s.AgeLimit);

    [HttpGet]
    public async Task<IActionResult> List(
        [FromQuery, Range(1, 10000)] int page = 1,
        [FromQuery, Range(1, 100)] int pageSize = 20,
        [FromQuery, StringLength(100)] string? search = null,
        CancellationToken ct = default) =>
        Ok(new { page, pageSize, items = (await sessions.ListAsync(User, page, pageSize, search, ct)).Select(Map) });

    [HttpGet("{id:guid}")]
    public async Task<IActionResult> Get(Guid id, CancellationToken ct) =>
        (await sessions.GetAsync(User, id, ct)) is { } row ? Ok(Map(row)) : NotFound();

    [Authorize(Roles = "Admin")]
    [HttpPost]
    public Task<IActionResult> Create([FromBody] WriteDto input, CancellationToken ct) => Safe(async () =>
    {
        var row = await sessions.CreateAsync(input.ToWrite(), ct);
        return CreatedAtAction(nameof(Get), new { id = row.Id }, Map(row));
    });

    [Authorize(Roles = "Admin")]
    [HttpPut("{id:guid}")]
    public Task<IActionResult> Replace(Guid id, [FromBody] WriteDto input, CancellationToken ct) => Safe(async () =>
        Ok(Map(await sessions.UpdateAsync(id, input.ToWrite(), ct))));

    [Authorize(Roles = "Admin")]
    [HttpDelete("{id:guid}")]
    public Task<IActionResult> Delete(Guid id, CancellationToken ct) => Safe(async () =>
    {
        await sessions.DeleteAsync(id, ct);
        return NoContent();
    });
}
