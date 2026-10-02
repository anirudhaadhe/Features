using System.Text.Json;
using Microsoft.AspNetCore.Hosting;
using Features.Application.Products;

namespace Features.Infrastructure.Products;

public sealed class JsonProductCatalog(IWebHostEnvironment environment) : IProductCatalog
{
    private IReadOnlyList<Product> _products = Array.AsReadOnly(Array.Empty<Product>());

    public Task<IReadOnlyList<Product>> GetAllAsync(CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        return Task.FromResult(Volatile.Read(ref _products));
    }

    public async Task<int> SeedAsync(CancellationToken cancellationToken = default)
    {
        var filePath = Path.Combine(environment.ContentRootPath, "Data", "products.json");
        await using var stream = File.OpenRead(filePath);
        var products = await JsonSerializer.DeserializeAsync<Product[]>(stream, cancellationToken: cancellationToken);
        var snapshot = Array.AsReadOnly(products ?? []);
        Interlocked.Exchange(ref _products, snapshot);

        return snapshot.Count;
    }
}
