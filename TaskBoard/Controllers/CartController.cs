using Microsoft.AspNetCore.Mvc;
using TaskBoard.Services;

namespace TaskBoard.Controllers;

public class CartController : Controller
{
    private readonly IStoreService _store;
    public CartController(IStoreService store) => _store = store;
    public IActionResult Index() => View(_store.GetCart());

    [HttpPost]
    public IActionResult Clear()
    {
        _store.ClearCart();
        return RedirectToAction(nameof(Index));
    }
}
