namespace Features.Application.Products;

public interface IProductCatalog
{
    Task<IReadOnlyList<Product>> GetAllAsync(CancellationToken cancellationToken = default);

    Task<int> SeedAsync(CancellationToken cancellationToken = default);
}
