namespace DashboardAdmin.Models;

public sealed class DashboardViewModel
{
    public required IEnumerable<DashboardCard> Cards { get; init; }
    public required IEnumerable<TrendSummaryViewModel> TrendSummaries { get; init; }
}

public sealed record TrendSummaryViewModel(Trend Trend, int Count, decimal TotalValue);
