using Features.Application.Products;
using Microsoft.OData.ModelBuilder;

namespace Features.OData;

public static class ProductEdmModel
{
    public static Microsoft.OData.Edm.IEdmModel GetEdmModel()
    {
        var builder = new ODataConventionModelBuilder();
        builder.EntitySet<Product>("Products");
        return builder.GetEdmModel();
    }
}
