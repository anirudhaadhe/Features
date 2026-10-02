using Features.Infrastructure;
using Features.Infrastructure.Caching;
using Features.Application.Events;
using Features.Application.Products;
using Features.Domain.Products;
using Features.Infrastructure.Products;
using Features.Infrastructure.Notifications;
using Features.OData;
using Features.Infrastructure.DataAccess;
using Features.Infrastructure.DataAccess.Sample;
using Microsoft.AspNetCore.OData;
using Microsoft.AspNetCore.OutputCaching;
using Scalar.AspNetCore;

var builder = WebApplication.CreateBuilder(args);

// Add services to the container.
// Learn more about configuring OpenAPI at https://aka.ms/aspnet/openapi
builder.Services.AddOpenApi();
builder.Services
    .AddControllers()
    .AddOData(options => options
        .Select()
        .Filter()
        .OrderBy()
        .Count()
        .SetMaxTop(100)
        .AddRouteComponents("odata", ProductEdmModel.GetEdmModel()));
builder.Services.AddEventInfrastructure(builder.Configuration);
builder.Services.AddNotificationServices(builder.Configuration);
builder.Services.AddRedisOutputCaching(builder.Configuration);
builder.Services.AddSqlServerMetadataDataAccess<MetadataDataContext>(builder.Configuration);
builder.Services.AddSingleton<IProductCatalog, JsonProductCatalog>();
builder.Services.AddAuthorization(options =>
{
    options.AddPolicy("WeatherForecastAccess", policy =>
    {
        policy.RequireAuthenticatedUser();
    });
});

var app = builder.Build();
_ = app.Services.GetRequiredService<IEntityMetadataResolver>();

if (app.Environment.IsDevelopment())
{
    await using var sampleScope = app.Services.CreateAsyncScope();
    var dataContext = sampleScope.ServiceProvider.GetRequiredService<MetadataDataContext>();
    if (await dataContext.Database.EnsureCreatedAsync())
    {
        var customer = new Customer
        {
            Name = "Ada Customer",
            Country = "IN",
            Email = "ada@domain.com"
        };
        var order = new Order
        {
            OrderDate = new DateOnly(2026, 10, 2),
            Status = "Open",
            Total = 1500,
            Customer = customer,
            Lines = [new OrderLine { ProductId = 101, Qty = 2 }]
        };
        dataContext.Add(order);
        await dataContext.SaveChangesAsync();
    }
}

var productCatalog = app.Services.GetRequiredService<IProductCatalog>();
await productCatalog.SeedAsync();

// Configure the HTTP request pipeline.
if (app.Environment.IsDevelopment())
{
    app.MapOpenApi();
    app.MapScalarApiReference();
    app.MapGet("/", () => Results.Redirect("/scalar/v1"))
        .ExcludeFromDescription();
}

app.UseHttpsRedirection();
app.UseAuthorization();
app.UseOutputCache();
app.MapControllers();

var summaries = new[]
{
    "Freezing", "Bracing", "Chilly", "Cool", "Mild", "Warm", "Balmy", "Hot", "Sweltering", "Scorching"
};

app.MapGet("/weatherforecast", () =>
{
    var forecast =  Enumerable.Range(1, 5).Select(index =>
        new WeatherForecast
        (
            DateOnly.FromDateTime(DateTime.Now.AddDays(index)),
            Random.Shared.Next(-20, 55),
            summaries[Random.Shared.Next(summaries.Length)]
        ))
        .ToArray();
    return forecast;
})
.WithName("GetWeatherForecast")
.RequireAuthorization("WeatherForecastAccess")
.CacheOutput("WeatherForecast");

app.MapGet("/api/products", async (IProductCatalog productCatalog, CancellationToken cancellationToken) =>
    Results.Ok(await productCatalog.GetAllAsync(cancellationToken)))
    .WithName("GetProducts")
    .WithSummary("Returns the 100 sample products loaded from the JSON data file.")
    .CacheOutput("Products");

if (app.Environment.IsDevelopment())
{
    app.MapPost("/api/products/seed", async (
        IProductCatalog productCatalog,
        IDomainEventDispatcher domainEventDispatcher,
        IOutputCacheStore outputCacheStore,
        CancellationToken cancellationToken) =>
    {
        var count = await productCatalog.SeedAsync(cancellationToken);
        await outputCacheStore.EvictByTagAsync("products", cancellationToken);
        await domainEventDispatcher.DispatchAsync(new ProductSampleDataSeededEvent(count), cancellationToken);
        return Results.Ok(new { LoadedRecords = count, Storage = "in-memory" });
    })
    .WithName("SeedProducts")
    .WithSummary("Reloads the product fixture into the in-memory catalog.");
}

app.Run();

record WeatherForecast(DateOnly Date, int TemperatureC, string? Summary)
{
    public int TemperatureF => 32 + (int)(TemperatureC / 0.5556);
}
