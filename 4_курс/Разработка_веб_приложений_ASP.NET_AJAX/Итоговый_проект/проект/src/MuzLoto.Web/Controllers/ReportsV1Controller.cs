using Microsoft.AspNetCore.Authentication.JwtBearer;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using MuzLoto.Web.Services;

namespace MuzLoto.Web.Controllers;

/// <summary>Отчёты продаж. Admin — по всем играм, Manager — по назначенным; User — 403.</summary>
[ApiController]
[IgnoreAntiforgeryToken]
[Authorize(AuthenticationSchemes = JwtBearerDefaults.AuthenticationScheme, Roles = "Admin,Manager")]
[Route("api/v1/reports")]
[ApiExplorerSettings(GroupName = "v1")]
public sealed class ReportsV1Controller(SalesReport report) : ControllerBase
{
    /// <summary>
    /// Сводка: продано билетов, подтверждённая выручка, число заказов, средний чек, среднее число билетов на заказ,
    /// валовые платежи, возвраты и чистые поступления. Неоплаченные заявки продажами не считаются.
    /// </summary>
    [HttpGet("summary")]
    [ProducesResponseType(typeof(SalesSummary), StatusCodes.Status200OK)]
    [ProducesResponseType(StatusCodes.Status401Unauthorized)]
    [ProducesResponseType(StatusCodes.Status403Forbidden)]
    public async Task<ActionResult<SalesSummary>> Summary(CancellationToken ct) =>
        Ok(await report.SummaryAsync(User, ct));

    /// <summary>Продажи по играм за период (по дате проведения): билетов, выручка за вычетом возвратов, заказов.</summary>
    /// <param name="from">Начало периода, yyyy-MM-dd (включительно).</param>
    /// <param name="to">Конец периода, yyyy-MM-dd (включительно).</param>
    [HttpGet("by-game")]
    [ProducesResponseType(typeof(IEnumerable<SalesRow>), StatusCodes.Status200OK)]
    public async Task<ActionResult<IEnumerable<SalesRow>>> ByGame([FromQuery] DateOnly? from, [FromQuery] DateOnly? to, CancellationToken ct) =>
        Ok(await report.ByGameAsync(User, new ReportPeriod(from, to), ct));

    /// <summary>Продажи по датам сеансов за период.</summary>
    [HttpGet("by-date")]
    [ProducesResponseType(typeof(IEnumerable<DateSalesRow>), StatusCodes.Status200OK)]
    public async Task<ActionResult<IEnumerable<DateSalesRow>>> ByDate([FromQuery] DateOnly? from, [FromQuery] DateOnly? to, CancellationToken ct) =>
        Ok(await report.ByDateAsync(User, new ReportPeriod(from, to), ct));

    /// <summary>Оперативный отчёт: будущие игры, свободные места и неоплаченные заявки.</summary>
    [HttpGet("operational")]
    [ProducesResponseType(typeof(IEnumerable<OperationalRow>), StatusCodes.Status200OK)]
    public async Task<ActionResult<IEnumerable<OperationalRow>>> Operational(CancellationToken ct) =>
        Ok(await report.OperationalAsync(User, ct));
}
