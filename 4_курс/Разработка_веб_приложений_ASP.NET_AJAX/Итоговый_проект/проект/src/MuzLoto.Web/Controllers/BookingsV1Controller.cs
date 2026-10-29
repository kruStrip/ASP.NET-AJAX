using System.ComponentModel.DataAnnotations;
using System.Security.Claims;
using Microsoft.AspNetCore.Authentication.JwtBearer;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;
using MuzLoto.Web.Data;
using MuzLoto.Web.Domain;
using MuzLoto.Web.Services;

namespace MuzLoto.Web.Controllers;

/// <summary>
/// Заявки через API. User видит и меняет только свои заявки, Manager — заявки назначенных ему игр, Admin — все.
/// Владелец новой заявки берётся из JWT, а не из тела запроса.
/// </summary>
[ApiController]
[IgnoreAntiforgeryToken]
[Authorize(AuthenticationSchemes = JwtBearerDefaults.AuthenticationScheme)]
[Route("api/v1/bookings")]
[ApiExplorerSettings(GroupName = "v1")]
public sealed class BookingsV1Controller(
    AppDbContext db, BookingWorkflow workflow, IAuthorizationService authorization) : ApiControllerBase
{
    public sealed record ReadDto(
        Guid Id, Guid EventId, string GameTitle, DateTimeOffset StartsAt, string TeamName,
        string CustomerName, string Phone, string? Telegram, int Quantity, decimal TicketPrice,
        decimal Total, string Status, bool IsOwn, DateTimeOffset CreatedAt, DateTimeOffset? ConfirmedAt);

    public sealed class CreateDto
    {
        /// <summary>Сеанс игры (id из /api/v1/events).</summary>
        [Required] public Guid EventId { get; set; }
        [Required, StringLength(150)] public string TeamName { get; set; } = string.Empty;
        [Required, StringLength(150)] public string CustomerName { get; set; } = string.Empty;
        /// <summary>Российский номер в любом формате, сохраняется как +7XXXXXXXXXX.</summary>
        [Required, StringLength(40)] public string Phone { get; set; } = string.Empty;
        /// <summary>@username или ссылка t.me.</summary>
        [StringLength(100)] public string? Telegram { get; set; }
        [Range(1, 100000)] public int Quantity { get; set; }
    }

    public sealed class UpdateDto
    {
        [Required, StringLength(150)] public string TeamName { get; set; } = string.Empty;
        [Required, StringLength(150)] public string CustomerName { get; set; } = string.Empty;
        [StringLength(100)] public string? Telegram { get; set; }
        [Range(1, 100000)] public int Quantity { get; set; }
    }

    private string UserId => User.FindFirstValue(ClaimTypes.NameIdentifier)!;

    /// <summary>Список доступных заявок (свои; для менеджера — также заявки назначенных игр; для админа — все).</summary>
    [HttpGet]
    [ProducesResponseType(typeof(IEnumerable<ReadDto>), StatusCodes.Status200OK)]
    public async Task<IActionResult> List(
        [FromQuery] BookingStatus? status = null,
        [FromQuery, Range(1, 10000)] int page = 1,
        [FromQuery, Range(1, 100)] int pageSize = 20,
        CancellationToken ct = default)
    {
        var query = Bookings();
        if (!User.IsInRole("Admin"))
        {
            var uid = UserId;
            query = User.IsInRole("Manager")
                ? query.Where(x => x.OwnerUserId == uid || x.GameSession.Staff.Any(a => a.UserId == uid))
                : query.Where(x => x.OwnerUserId == uid);
        }
        if (status.HasValue) query = query.Where(x => x.Status == status);
        var rows = await query.OrderByDescending(x => x.CreatedAt).ThenBy(x => x.Id)
            .Skip((page - 1) * pageSize).Take(pageSize).ToListAsync(ct);
        return Ok(rows.Select(Map));
    }

    /// <summary>Карточка заявки. Чужая заявка — 403.</summary>
    [HttpGet("{id:guid}")]
    [ProducesResponseType(typeof(ReadDto), StatusCodes.Status200OK)]
    [ProducesResponseType(StatusCodes.Status403Forbidden)]
    [ProducesResponseType(StatusCodes.Status404NotFound)]
    public async Task<IActionResult> Get(Guid id, CancellationToken ct)
    {
        var booking = await Bookings().SingleOrDefaultAsync(x => x.Id == id, ct);
        if (booking is null) return NotFound();
        if (!await AllowedAsync(booking, BookingPolicies.CanViewBooking)) return Forbid();
        return Ok(Map(booking));
    }

    /// <summary>
    /// Создаёт неоплаченную заявку от имени владельца токена. Места не резервируются до подтверждения оплаты.
    /// Повтор с тем же заголовком Idempotency-Key возвращает уже созданную заявку (200).
    /// </summary>
    [HttpPost]
    [ProducesResponseType(typeof(ReadDto), StatusCodes.Status201Created)]
    [ProducesResponseType(typeof(ReadDto), StatusCodes.Status200OK)]
    [ProducesResponseType(typeof(ValidationProblemDetails), StatusCodes.Status400BadRequest)]
    [ProducesResponseType(typeof(ProblemDetails), StatusCodes.Status409Conflict)]
    public Task<IActionResult> Create([FromBody] CreateDto input,
        [FromHeader(Name = "Idempotency-Key"), StringLength(100)] string? idempotencyKey,
        CancellationToken ct) => Safe(async () =>
    {
        var phone = ContactRules.NormalizePhone(input.Phone);
        var telegram = ContactRules.NormalizeTelegram(input.Telegram);
        if (phone is null) ModelState.AddModelError(nameof(input.Phone), "Некорректный номер телефона.");
        if (!string.IsNullOrWhiteSpace(input.Telegram) && telegram is null)
            ModelState.AddModelError(nameof(input.Telegram), "Ник Telegram в формате @username.");
        if (!ModelState.IsValid) return ValidationProblem(ModelState);

        // The key is scoped to the caller so two users can never collide on the same value.
        var key = $"api-{UserId}-{(string.IsNullOrWhiteSpace(idempotencyKey) ? Guid.NewGuid().ToString("N") : idempotencyKey.Trim())}";
        var (bookingId, duplicate) = await workflow.ReceiveAsync(new IncomingBooking(
            key, input.EventId, input.TeamName, input.CustomerName, phone!, telegram, input.Quantity,
            Provider: BookingSources.Api, OwnerUserId: UserId), ct);
        var created = await Bookings().SingleAsync(x => x.Id == bookingId, ct);
        return duplicate ? Ok(Map(created)) : CreatedAtAction(nameof(Get), new { id = bookingId }, Map(created));
    });

    /// <summary>Изменяет неоплаченную заявку: владелец, админ или назначенный менеджер. Чужая — 403.</summary>
    [HttpPut("{id:guid}")]
    [ProducesResponseType(typeof(ReadDto), StatusCodes.Status200OK)]
    [ProducesResponseType(typeof(ValidationProblemDetails), StatusCodes.Status400BadRequest)]
    [ProducesResponseType(StatusCodes.Status403Forbidden)]
    [ProducesResponseType(StatusCodes.Status404NotFound)]
    [ProducesResponseType(typeof(ProblemDetails), StatusCodes.Status409Conflict)]
    public Task<IActionResult> Update(Guid id, [FromBody] UpdateDto input, CancellationToken ct) => Safe(async () =>
    {
        var booking = await Bookings().SingleOrDefaultAsync(x => x.Id == id, ct);
        if (booking is null) return NotFound();
        if (!await AllowedAsync(booking, BookingPolicies.CanEditBooking))
        {
            // Visible but no longer editable (e.g. own paid booking) is a conflict, not a permission problem.
            return await AllowedAsync(booking, BookingPolicies.CanViewBooking)
                ? Problem(statusCode: 409, title: "Операция отклонена", detail: "Изменить можно только неоплаченную заявку.")
                : Forbid();
        }
        var telegram = ContactRules.NormalizeTelegram(input.Telegram);
        if (!string.IsNullOrWhiteSpace(input.Telegram) && telegram is null)
        {
            ModelState.AddModelError(nameof(input.Telegram), "Ник Telegram в формате @username.");
            return ValidationProblem(ModelState);
        }
        await workflow.UpdatePendingAsync(id, input.TeamName, input.CustomerName, telegram, input.Quantity, ct);
        return Ok(Map(await Bookings().SingleAsync(x => x.Id == id, ct)));
    });

    private IQueryable<Booking> Bookings() =>
        db.Bookings.AsNoTracking().Include(x => x.GameSession).ThenInclude(x => x.Game);

    private async Task<bool> AllowedAsync(Booking booking, string policy) =>
        (await authorization.AuthorizeAsync(User, booking, policy)).Succeeded;

    private ReadDto Map(Booking b) => new(
        b.Id, b.GameSessionId, b.GameSession.Game.Title, b.GameSession.StartsAt, b.TeamName,
        b.CustomerName, b.Phone, b.Telegram, b.Quantity, b.TicketPriceSnapshot,
        b.Quantity * b.TicketPriceSnapshot, b.Status.ToString().ToLowerInvariant(),
        b.OwnerUserId == UserId, b.CreatedAt, b.ConfirmedAt);
}
