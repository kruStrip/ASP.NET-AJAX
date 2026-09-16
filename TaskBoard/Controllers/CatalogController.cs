using Microsoft.AspNetCore.Mvc;
using TaskBoard.Services;

namespace TaskBoard.Controllers;

public class CatalogController : Controller
{
    private readonly IStoreService _store;
    public CatalogController(IStoreService store) => _store = store;

    public IActionResult Index(string? category)
    {
        ViewBag.Category = category;
        ViewBag.Categories = new[] { "Электроника", "Одежда", "Книги" };
        return View(_store.GetProducts(category));
    }

    [HttpPost]
    public IActionResult AddToCart(int id, string? category)
    {
        var added = _store.AddToCart(id);
        TempData["CartMessage"] = added ? "Товар добавлен в корзину" : "Товар недоступен";
        TempData["CartMessageType"] = added ? "success" : "warning";
        return RedirectToAction(nameof(Index), new { category });
    }
}
