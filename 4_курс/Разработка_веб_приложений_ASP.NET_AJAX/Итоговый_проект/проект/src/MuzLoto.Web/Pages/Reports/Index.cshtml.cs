using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Mvc.RazorPages;
using MuzLoto.Web.Services;

namespace MuzLoto.Web.Pages.Reports;

[Authorize(Roles = "Admin")]
public class IndexModel(SalesReport report) : PageModel
{
    [BindProperty(SupportsGet = true)] public DateOnly? From { get; set; }
    [BindProperty(SupportsGet = true)] public DateOnly? To { get; set; }

    public SalesSummary Summary { get; private set; } = null!;
    public IReadOnlyList<SalesRow> ByGame { get; private set; } = [];
    public IReadOnlyList<DateSalesRow> ByDate { get; private set; } = [];
    public IReadOnlyList<OperationalRow> Operational { get; private set; } = [];

    public async Task OnGetAsync(CancellationToken ct)
    {
        if (From > To) (From, To) = (To, From);
        var period = new ReportPeriod(From, To);
        Summary = await report.SummaryAsync(User, ct);
        ByGame = await report.ByGameAsync(User, period, ct);
        ByDate = await report.ByDateAsync(User, period, ct);
        Operational = await report.OperationalAsync(User, ct);
    }
}
