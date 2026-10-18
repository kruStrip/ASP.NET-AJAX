using DashboardAdmin.Models;
using DashboardAdmin.Services;
using Microsoft.AspNetCore.Mvc;

namespace DashboardAdmin.ViewComponents;

public sealed class TrendSummaryViewComponent(IDashboardRepository repository) : ViewComponent
{
    public IViewComponentResult Invoke()
    {
        var summaries = repository.GetAllCards()
            .GroupBy(card => card.Trend)
            .Select(group => new TrendSummaryViewModel(group.Key, group.Count(), group.Sum(card => card.Value)))
            .OrderBy(summary => summary.Trend)
            .ToList();

        return View(summaries);
    }
}
