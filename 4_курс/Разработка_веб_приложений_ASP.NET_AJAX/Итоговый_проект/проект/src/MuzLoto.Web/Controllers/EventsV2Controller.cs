using System.ComponentModel.DataAnnotations;
using Microsoft.AspNetCore.Authentication.JwtBearer;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using MuzLoto.Web.Services;

namespace MuzLoto.Web.Controllers;

[ApiController]
[IgnoreAntiforgeryToken]
[Authorize(AuthenticationSchemes = JwtBearerDefaults.AuthenticationScheme, Roles = "Admin,Manager")]
[Route("api/v2/events")]
[ApiExplorerSettings(GroupName = "v2")]
public sealed class EventsV2Controller(SessionCrud sessions) : ApiControllerBase
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
        /// <summary>Ссылка на место в 2ГИС (только http/https).</summary>
        [StringLength(500)] public string? MapUrl { get; set; }
        public SessionWrite ToWrite() => new(GameId, StartsAt, Location.TimeZone, Location.Name,
            Seats.Total, TicketPrice, DurationMinutes, AgeLimit, SalesOpen, MapUrl, SetMapUrl: true);
    }
    public sealed record AvailabilityDto(int Total, int Sold, int Remaining);
    /// <summary>Контракт v2: вложенные location/availability, статус строкой, плюс remainingTickets и mapUrl.</summary>
    public sealed record ReadDto(Guid Id, Guid GameId, string GameTitle, DateTimeOffset StartsAt,
        LocationDto Location, AvailabilityDto Availability, int RemainingTickets, string? MapUrl,
        decimal TicketPrice, string Status, int DurationMinutes, int AgeLimit);
    private static ReadDto Map(SessionSnapshot s) => new(
        s.Id, s.GameId, s.GameTitle, s.StartsAt,
        new LocationDto { Name = s.Venue, TimeZone = s.TimeZoneId },
        new AvailabilityDto(s.Capacity, s.Reserved, Math.Max(0, s.Capacity - s.Reserved)),
        Math.Max(0, s.Capacity - s.Reserved), s.MapUrl,
        s.TicketPrice, s.Completed ? "completed" : s.SalesOpen ? "open" : "closed",
        s.DurationMinutes, s.AgeLimit);

    /// <summary>Список доступных мероприятий: админ — все, менеджер — назначенные.</summary>
    [HttpGet]
    [ProducesResponseType(typeof(IEnumerable<ReadDto>), StatusCodes.Status200OK)]
    public async Task<IActionResult> List(
        [FromQuery, Range(1, 10000)] int page = 1,
        [FromQuery, Range(1, 100)] int pageSize = 20,
        [FromQuery, StringLength(100)] string? search = null,
        CancellationToken ct = default) =>
        Ok(new { page, pageSize, items = (await sessions.ListAsync(User, page, pageSize, search, ct)).Select(Map) });

    /// <summary>Одно мероприятие. Чужое или несуществующее — 404.</summary>
    [HttpGet("{id:guid}")]
    [ProducesResponseType(typeof(ReadDto), StatusCodes.Status200OK)]
    [ProducesResponseType(StatusCodes.Status404NotFound)]
    public async Task<IActionResult> Get(Guid id, CancellationToken ct) =>
        (await sessions.GetAsync(User, id, ct)) is { } row ? Ok(Map(row)) : NotFound();

    /// <summary>Создаёт сеанс игры (Admin). 201 + Location.</summary>
    [Authorize(Roles = "Admin")]
    [HttpPost]
    [ProducesResponseType(typeof(ReadDto), StatusCodes.Status201Created)]
    [ProducesResponseType(typeof(ValidationProblemDetails), StatusCodes.Status400BadRequest)]
    [ProducesResponseType(StatusCodes.Status403Forbidden)]
    [ProducesResponseType(typeof(ProblemDetails), StatusCodes.Status404NotFound)]
    public Task<IActionResult> Create([FromBody] WriteDto input, CancellationToken ct) => Safe(async () =>
    {
        var row = await sessions.CreateAsync(input.ToWrite(), ct);
        return CreatedAtAction(nameof(Get), new { id = row.Id }, Map(row));
    });

    /// <summary>Полностью заменяет параметры сеанса (Admin). Вместимость не может быть меньше оплаченных мест — 409.</summary>
    [Authorize(Roles = "Admin")]
    [HttpPut("{id:guid}")]
    [ProducesResponseType(typeof(ReadDto), StatusCodes.Status200OK)]
    [ProducesResponseType(typeof(ValidationProblemDetails), StatusCodes.Status400BadRequest)]
    [ProducesResponseType(typeof(ProblemDetails), StatusCodes.Status404NotFound)]
    [ProducesResponseType(typeof(ProblemDetails), StatusCodes.Status409Conflict)]
    public Task<IActionResult> Replace(Guid id, [FromBody] WriteDto input, CancellationToken ct) => Safe(async () =>
        Ok(Map(await sessions.UpdateAsync(id, input.ToWrite(), ct))));

    /// <summary>Удаляет сеанс без заявок (Admin); обложки и назначения удаляются каскадно. С заявками — 409.</summary>
    [Authorize(Roles = "Admin")]
    [HttpDelete("{id:guid}")]
    [ProducesResponseType(StatusCodes.Status204NoContent)]
    [ProducesResponseType(typeof(ProblemDetails), StatusCodes.Status404NotFound)]
    [ProducesResponseType(typeof(ProblemDetails), StatusCodes.Status409Conflict)]
    public Task<IActionResult> Delete(Guid id, CancellationToken ct) => Safe(async () =>
    {
        await sessions.DeleteAsync(id, ct);
        return NoContent();
    });
}
