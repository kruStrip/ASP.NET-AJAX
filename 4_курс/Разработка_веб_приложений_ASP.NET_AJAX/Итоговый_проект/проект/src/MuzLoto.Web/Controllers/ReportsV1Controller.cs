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
}
