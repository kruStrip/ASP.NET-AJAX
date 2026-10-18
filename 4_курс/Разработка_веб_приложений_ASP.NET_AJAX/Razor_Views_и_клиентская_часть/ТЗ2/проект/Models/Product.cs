namespace TaskBoard.Models;

public class Product
{
    public int Id { get; set; }
    public string Name { get; set; } = "";
    public string Description { get; set; } = "";
    public string Category { get; set; } = "";
    public decimal Price { get; set; }
    public decimal? OldPrice { get; set; }
    public string Image { get; set; } = "";
    public bool IsHit { get; set; }
    public int Stock { get; set; }
}

public class CartItem
{
    public Product Product { get; set; } = new();
    public int Quantity { get; set; }
    public decimal Total => Product.Price * Quantity;
}

public class CartViewModel
{
    public List<CartItem> Items { get; set; } = new();
    public decimal TotalAmount => Items.Sum(item => item.Total);
}
