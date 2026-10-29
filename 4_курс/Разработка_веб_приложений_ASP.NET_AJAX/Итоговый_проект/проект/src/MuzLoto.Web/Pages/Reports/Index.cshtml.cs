using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc.RazorPages;
using MuzLoto.Web.Services;

namespace MuzLoto.Web.Pages.Reports;

[Authorize(Roles = "Admin")]
public class IndexModel(SalesReport report) : PageModel
{
    public SalesSummary Summary { get; private set; } = null!;

    public async Task OnGetAsync(CancellationToken ct) => Summary = await report.SummaryAsync(User, ct);
}
