namespace Features.Application.Products;

public sealed record Product(
    int Id,
    string Name,
    string Category,
    decimal Price,
    int Stock);
