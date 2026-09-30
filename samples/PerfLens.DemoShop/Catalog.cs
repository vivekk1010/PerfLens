namespace PerfLens.DemoShop;

public sealed record Product(int Id, string Name, string Category, decimal Price, double Rating, string Description)
{
    public string Image => $"/img/product/{Id}.svg";
}

public sealed record Review(int Id, int ProductId, string Author, int Stars, string Text, string Date);

public sealed record Order(string Id, string Date, string Status, int Items, decimal Total);

/// <summary>Fixed, deterministic demo data (20 products, generated reviews and orders).</summary>
public sealed class Catalog
{
    public const int ProductCount = 20;

    private static readonly string[] Names =
    [
        "Aurora Headphones", "Basalt Desk Lamp", "Cedar Camping Mug", "Delta Notebook", "Ember Chef Knife",
        "Fjord Speaker", "Granite Planter", "Harbor Backpack", "Iris Monitor Arm", "Juniper Teapot",
        "Kestrel Earbuds", "Lumen Floor Lamp", "Maple Hiking Poles", "Nimbus Office Chair", "Olive Cutting Board",
        "Pulse Turntable", "Quartz Vase", "Ridge Tent", "Summit Keyboard", "Tidal Coffee Grinder",
    ];

    private static readonly string[] Categories = ["Audio", "Home", "Outdoor", "Office", "Kitchen"];
    private static readonly string[] Authors = ["Asha", "Ben", "Chen", "Dana", "Eli", "Farah", "Goran", "Hana"];
    private static readonly string[] Phrases =
    [
        "Exactly as described.", "Great value for the price.", "Shipping was quick.",
        "Build quality could be better.", "Would buy again.", "Looks even better in person.",
    ];
    private static readonly string[] Statuses = ["Delivered", "Shipped", "Processing", "Delivered", "Cancelled"];

    public IReadOnlyList<Product> Products { get; } = Enumerable.Range(1, ProductCount).Select(i => new Product(
        i,
        Names[i - 1],
        Categories[(i - 1) % Categories.Length],
        9.99m + i * 7.5m,
        Math.Round(3.2 + (i * 37 % 18) / 10.0, 1),
        $"{Names[i - 1]} is a demo product in the {Categories[(i - 1) % Categories.Length]} range. " +
        "It exists so PerfLens has realistic content, images and API calls to measure.")).ToList();

    public Product? Find(int id) => id is >= 1 and <= ProductCount ? Products[id - 1] : null;

    public IReadOnlyList<Review> ReviewsFor(int productId) =>
        Enumerable.Range(1, 3 + productId % 4).Select(n => new Review(
            productId * 100 + n,
            productId,
            Authors[(productId + n) % Authors.Length],
            1 + (productId * 7 + n * 3) % 5,
            Phrases[(productId + n * 2) % Phrases.Length],
            new DateOnly(2026, 1 + (productId + n) % 12, 1 + (n * 5) % 28).ToString("yyyy-MM-dd"))).ToList();

    public IReadOnlyList<Order> OrdersFor(string user) =>
        Enumerable.Range(1, 8).Select(n => new Order(
            $"{user.ToUpperInvariant()}-{1000 + n * 17}",
            new DateOnly(2026, 1 + n % 9, 1 + (n * 3) % 28).ToString("yyyy-MM-dd"),
            Statuses[n % Statuses.Length],
            1 + n % 4,
            19.99m * (1 + n % 4) + n)).ToList();

    public IReadOnlyList<Product> Search(string? query) =>
        string.IsNullOrWhiteSpace(query)
            ? Products
            : Products.Where(p => p.Name.Contains(query, StringComparison.OrdinalIgnoreCase)
                                  || p.Category.Contains(query, StringComparison.OrdinalIgnoreCase)).ToList();
}
