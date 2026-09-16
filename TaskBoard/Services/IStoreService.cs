using TaskBoard.Models;

namespace TaskBoard.Services;

public interface IStoreService
{
    IReadOnlyList<Product> GetProducts(string? category = null);
    Product? GetProduct(int id);
    CartViewModel GetCart();
    bool AddToCart(int id);
    void ClearCart();
}

public class InMemoryStoreService : IStoreService
{
    private readonly List<Product> _products =
    [
        new() { Id = 1, Name = "Наушники Wave", Description = "Беспроводной звук и активное шумоподавление.", Category = "Электроника", Price = 7990, OldPrice = 9990, Image = "/img/product-headphones.svg", IsHit = true, Stock = 8 },
        new() { Id = 2, Name = "Рюкзак Urban", Description = "Лёгкий городской рюкзак с отделением для ноутбука.", Category = "Одежда", Price = 4590, Image = "/img/product-backpack.svg", Stock = 12 },
        new() { Id = 3, Name = "Книга «Фокус»", Description = "Практическое руководство по концентрации и продуктивности.", Category = "Книги", Price = 1290, OldPrice = 1890, Image = "/img/product-book.svg", IsHit = true, Stock = 0 },
        new() { Id = 4, Name = "Клавиатура Key", Description = "Механическая клавиатура для работы и игр.", Category = "Электроника", Price = 6490, Image = "/img/product-keyboard.svg", Stock = 5 },
        new() { Id = 5, Name = "Худи Basic", Description = "Мягкий хлопковый свитшот на каждый день.", Category = "Одежда", Price = 3290, OldPrice = 4990, Image = "/img/product-hoodie.svg", Stock = 3 },
        new() { Id = 6, Name = "Ежедневник План", Description = "Недатированный ежедневник с удобной разметкой.", Category = "Книги", Price = 890, Image = "/img/product-notebook.svg", Stock = 20 }
    ];
    private readonly List<CartItem> _cart = new();
    public IReadOnlyList<Product> GetProducts(string? category = null) => string.IsNullOrWhiteSpace(category) ? _products : _products.Where(p => p.Category == category).ToList();
    public Product? GetProduct(int id) => _products.FirstOrDefault(p => p.Id == id);
    public CartViewModel GetCart() => new() { Items = _cart.ToList() };
    public bool AddToCart(int id)
    {
        var product = GetProduct(id);
        if (product is null || product.Stock == 0) return false;
        var item = _cart.FirstOrDefault(i => i.Product.Id == id);
        if (item is null) _cart.Add(new CartItem { Product = product, Quantity = 1 });
        else if (item.Quantity < product.Stock) item.Quantity++;
        return true;
    }
    public void ClearCart() => _cart.Clear();
}
