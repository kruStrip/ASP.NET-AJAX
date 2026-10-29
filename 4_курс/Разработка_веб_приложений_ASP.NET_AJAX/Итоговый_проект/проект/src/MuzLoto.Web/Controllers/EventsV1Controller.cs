using System.ComponentModel.DataAnnotations;
using Microsoft.AspNetCore.Authentication.JwtBearer;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using MuzLoto.Web.Services;

namespace MuzLoto.Web.Controllers;

[ApiController]
[IgnoreAntiforgeryToken]
[Authorize(AuthenticationSchemes = JwtBearerDefaults.AuthenticationScheme, Roles = "Admin,Manager")]
[Route("api/v1/events")]
[ApiExplorerSettings(GroupName = "v1")]
public sealed class EventsV1Controller(SessionCrud sessions) : ApiControllerBase
{
    /// <summary>Контракт v1: плоские поля Capacity, Venue и Reserved.</summary>
    public sealed class WriteDto
    {
        [Required] public Guid GameId { get; set; }
        [Required] public DateTimeOffset StartsAt { get; set; }
        [Required, StringLength(100)] public string TimeZoneId { get; set; } = "Europe/Moscow";
        [Required, StringLength(250)] public string Venue { get; set; } = string.Empty;
        [Range(1, 100000)] public int Capacity { get; set; }
        [Range(typeof(decimal), "0", "100000000")] public decimal TicketPrice { get; set; }
        [Range(1, 1440)] public int DurationMinutes { get; set; } = 120;
        [Range(0, 21)] public int AgeLimit { get; set; } = 12;
        public bool SalesOpen { get; set; } = true;
        public SessionWrite ToWrite() => new(GameId, StartsAt, TimeZoneId, Venue, Capacity,
            TicketPrice, DurationMinutes, AgeLimit, SalesOpen);
    }
    public sealed record ReadDto(
        Guid Id, Guid GameId, string GameTitle, DateTimeOffset StartsAt,
        string Venue, string TimeZoneId, int Capacity, int Reserved,
        decimal TicketPrice, bool SalesOpen, bool Completed);
    private static ReadDto Map(SessionSnapshot s) => new(
        s.Id, s.GameId, s.GameTitle, s.StartsAt, s.Venue, s.TimeZoneId,
        s.Capacity, s.Reserved, s.TicketPrice, s.SalesOpen, s.Completed);

    /// <summary>Список доступных мероприятий: админ — все, менеджер — назначенные.</summary>
    [HttpGet]
    [ProducesResponseType(typeof(IEnumerable<ReadDto>), StatusCodes.Status200OK)]
    public async Task<IActionResult> List(
        [FromQuery, Range(1, 10000)] int page = 1,
        [FromQuery, Range(1, 100)] int pageSize = 20,
        CancellationToken ct = default)
        => Ok((await sessions.ListAsync(User, page, pageSize, null, ct)).Select(Map));

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
        var created = await sessions.CreateAsync(input.ToWrite(), ct);
        return CreatedAtAction(nameof(Get), new { id = created.Id }, Map(created));
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
