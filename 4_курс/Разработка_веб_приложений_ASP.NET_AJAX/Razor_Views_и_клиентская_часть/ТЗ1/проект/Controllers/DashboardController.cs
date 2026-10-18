using DashboardAdmin.Services;
using Microsoft.AspNetCore.Mvc;

namespace DashboardAdmin.Controllers;

public sealed class DashboardController(IDashboardRepository repository) : Controller
{
    public IActionResult Index()
    {
        SetReportData();
        return View(repository.GetAllCards());
    }

    public IActionResult Print()
    {
        SetReportData();
        ViewData["Mode"] = "Print";
        return View("Index", repository.GetAllCards());
    }

    private void SetReportData()
    {
        ViewData["ReportPeriod"] = "Сентябрь 2026";
        ViewBag.Currency = "RUB";
    }
}
