using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Http;
using TaskBoard.Models;
using TaskBoard.Services;

namespace TaskBoard.Controllers;

public class CartController : Controller
{
    private readonly IStoreService _store;
    public CartController(IStoreService store) => _store = store;
    public IActionResult Index()
    {
        var items = ReadCartItems().GroupBy(id => id).Select(group => new CartItem
        {
            Product = _store.GetProduct(group.Key) ?? new Product(),
            Quantity = group.Count()
        }).Where(item => item.Product.Id != 0).ToList();
        return View(new CartViewModel { Items = items });
    }

    [HttpPost]
    public IActionResult Clear()
    {
        HttpContext.Session.Remove("Cart");
        return RedirectToAction(nameof(Index));
    }

    private List<int> ReadCartItems()
    {
        var cart = HttpContext.Session.GetString("Cart");
        return string.IsNullOrWhiteSpace(cart) ? new List<int>() : cart
            .Split(',', StringSplitOptions.RemoveEmptyEntries)
            .Where(value => int.TryParse(value, out _)).Select(int.Parse).ToList();
    }
}
