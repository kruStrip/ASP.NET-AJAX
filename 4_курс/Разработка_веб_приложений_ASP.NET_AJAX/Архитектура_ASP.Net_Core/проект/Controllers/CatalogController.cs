using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Http;
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

    [HttpGet]
    public IActionResult Search(string? query)
    {
        var products = _store.GetProducts();
        if (!string.IsNullOrWhiteSpace(query))
        {
            query = query.Trim();
            products = products.Where(product =>
                product.Name.Contains(query, StringComparison.OrdinalIgnoreCase) ||
                product.Description.Contains(query, StringComparison.OrdinalIgnoreCase)).ToList();
        }
        return PartialView("_ProductList", products);
    }

    [HttpPost]
    public IActionResult AddToCart(int id)
    {
        var product = _store.GetProduct(id);
        if (product is null || product.Stock == 0)
            return Json(new { success = false, message = "Товар недоступен" });

        var items = GetCartItems();
        items.Add(id);
        SaveCartItems(items);
        return Json(new { success = true, cartCount = items.Count, productName = product.Name });
    }

    [HttpGet]
    public IActionResult GetCartCount() => Json(new { count = GetCartItems().Count });

    private List<int> GetCartItems()
    {
        var cart = HttpContext.Session.GetString("Cart");
        return string.IsNullOrWhiteSpace(cart) ? new List<int>() : cart
            .Split(',', StringSplitOptions.RemoveEmptyEntries)
            .Where(value => int.TryParse(value, out _)).Select(int.Parse).ToList();
    }

    private void SaveCartItems(IEnumerable<int> items)
    {
        HttpContext.Session.SetString("Cart", string.Join(',', items));
    }
}
