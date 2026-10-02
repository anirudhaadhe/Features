using Features.Application.Products;
using Microsoft.AspNetCore.OData.Query;
using Microsoft.AspNetCore.OData.Routing.Controllers;
using Microsoft.AspNetCore.Mvc;

namespace Features.Controllers;

public sealed class ProductsController(IProductCatalog productCatalog) : ODataController
{
    [EnableQuery(AllowedQueryOptions = AllowedQueryOptions.Filter
        | AllowedQueryOptions.OrderBy
        | AllowedQueryOptions.Select
        | AllowedQueryOptions.Count
        | AllowedQueryOptions.Top)]
    public async Task<IActionResult> Get(CancellationToken cancellationToken)
    {
        var products = await productCatalog.GetAllAsync(cancellationToken);
        return Ok(products.AsQueryable());
    }
}
