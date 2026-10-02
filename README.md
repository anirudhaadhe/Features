# Features API

A starter ASP.NET Core Minimal API targeting .NET 10.

## Run

Requirements: .NET 10 SDK. Redis is needed for endpoints that use the Redis output-cache policies.

```sh
dotnet restore
dotnet run --launch-profile Scalar
```

The `Scalar` launch profile sets the environment to Development, listens on port `5000`, and opens Scalar at <http://localhost:5000/scalar/v1>. The OpenAPI document is at <http://localhost:5000/openapi/v1.json>. The `https` profile uses port `7137` for HTTPS and `5000` for HTTP.

To start Redis locally with Docker, run:

```sh
docker run --name features-redis -p 6379:6379 -d redis:7-alpine
```

Scalar and the OpenAPI document do not require Redis, but cached API requests do. The weather endpoint also requires an authenticated user; this starter project does not configure an authentication provider.

### Run from Visual Studio

1. Set `Features` as the startup project.
2. Select the `Scalar` launch profile in the launch-profile dropdown.
3. Start the project with **F5** or **Start**. The browser opens Scalar automatically.
4. If the browser does not open, navigate to <http://localhost:5000/scalar/v1>.

Use a Development launch profile. Running with `--no-launch-profile` does not set `ASPNETCORE_ENVIRONMENT=Development`, so Scalar and the OpenAPI endpoint are not mapped.

The API exposes `GET /weatherforecast`, `GET /api/products`, and the OData product endpoints described below.

Use `Features.http` to send a sample request from an editor that supports HTTP files.

### Sample products API

`GET /api/products` returns 100 prepopulated sample products from `Data/products.json`. The JSON file is copied to build and publish output. This endpoint uses the `Products` Redis output-cache policy configured under `OutputCache:Policies` in `appsettings.json`.

The catalog loads the fixture into process memory at startup. In Development, `POST /api/products/seed` reloads the fixture into memory and invalidates cached product responses. The in-memory catalog resets to the fixture whenever the application restarts and is not shared between app instances; Redis stores only cached HTTP responses, not the product data.

Run the application in Development and open `http://localhost:5000/scalar/v1` to browse and try the API. The generated OpenAPI document is at `http://localhost:5000/openapi/v1.json`.

### OData products API

The web host exposes the product catalog as a read-only OData v4 entity set. The OData package, EDM, and controller are kept in the ASP.NET Core API project; they use the existing `IProductCatalog` and `Product` contracts, so the Domain, Application, and Infrastructure projects do not depend on OData.

- `GET /odata/Products` returns the product collection.
- `GET /odata/$metadata` returns the OData EDMX metadata. `Product` is keyed by `Id` and includes `Name`, `Category`, `Price`, and `Stock`.
- `GET /odata/Products` supports `$filter`, `$orderby`, `$select`, `$count`, and `$top`. `$top` is limited to 100; other query options are not enabled.

For example, request the ten most expensive products priced above 50, return only selected fields, and include the total matching count:

```http
GET /odata/Products?$filter=Price%20gt%2050&$orderby=Price%20desc&$select=Id,Name,Price&$count=true&$top=10
```

OData queries run against the in-memory product snapshot. They do not use Redis output caching. The existing `GET /api/products` route and its `Products` output-cache policy remain available and unchanged. The OData API currently supports reads only; product writes continue to be handled by the Development-only seed endpoint rather than OData CRUD operations.

Use the OData requests in `Features.http` or browse to `http://localhost:5000/odata/$metadata` while running the `Scalar` Development profile.

## Event infrastructure

The event infrastructure is organized into `Domain`, `Application`, and `Infrastructure` folders. Domain events are dispatched to registered `IDomainEventHandler<TDomainEvent>` implementations by the in-process dispatcher. Integration events have an `IIntegrationEventPublisher` application contract; provide a transport-specific implementation to connect a message broker.

### Configuration

`appsettings.json` can configure handler assembly scanning and retry behavior:

```json
{
  "EventInfrastructure": {
	"HandlerAssemblyNames": [ "Features" ],
	"MaxRetryAttempts": 2,
	"RetryDelay": "00:00:00.250"
  }
}
```

`MaxRetryAttempts` is the number of retries after the initial attempt. Options can also be configured in code. Code configuration is applied after configuration-file binding and overrides matching values:

```csharp
builder.Services.AddEventInfrastructure(
	builder.Configuration,
	options => options.MaxRetryAttempts = 3,
	typeof(Program).Assembly);
```

Assemblies can be scanned via `HandlerAssemblyNames` or supplied directly as above. Individual handlers can instead be registered explicitly. Filters and observers are registered explicitly:

```csharp
builder.Services
	.AddDomainEventHandler<OrderPlacedEvent, OrderPlacedHandler>()
	.AddDomainEventFilter<IgnoreTestOrderFilter>()
	.AddDomainEventObserver<LoggingDomainEventObserver>();
```

Implement handlers using `IDomainEventHandler<TDomainEvent>` and filters using `IDomainEventFilter`. All registered filters must allow an event for dispatch to proceed. Implement `IDomainEventObserver` to receive dispatch lifecycle observations; observer failures are logged and do not interrupt dispatch.

Dispatch an event through `IDomainEventDispatcher`:

```csharp
public sealed record OrderPlacedEvent(Guid OrderId) : DomainEvent;

public sealed class OrderPlacedHandler : IDomainEventHandler<OrderPlacedEvent>
{
	public Task HandleAsync(OrderPlacedEvent domainEvent, CancellationToken cancellationToken = default)
	{
		// Handle the event.
		return Task.CompletedTask;
	}
}
```

Handlers may run again when retries are enabled, so make retryable handlers idempotent. No broker-specific package or transport is included; implement and register `IIntegrationEventPublisher` for the chosen broker.

## Notifications

`INotificationService` provides generic application-level notifications. The initial provider writes structured notification details to the application log. Call it from an application service with any payload:

```csharp
await notificationService.NotifyAsync(
	new NotificationMessage("Order", "An order was created.", new { OrderId = orderId }),
	cancellationToken);
```

Domain-event notifications are configured by full event type name in `appsettings.json` and are emitted after dispatch completes:

```json
{
  "Notifications": {
	"Enabled": true,
	"DomainEventMappings": {
	  "Features.Domain.Products.ProductSampleDataSeededEvent": {
		"Category": "SampleData",
		"Message": "Sample product data was loaded into memory."
	  }
	}
  }
}
```

Set `Notifications:Enabled` to `false` to turn off both direct and domain-event notifications. The Development-only `POST /api/products/seed` dispatches the mapped sample event. Notifications currently go to logs; additional delivery providers can be added behind `INotificationService`.

## Request authorization

HTTP authorization is handled separately from domain-event filters. `Program.cs` registers the `WeatherForecastAccess` policy and applies it only to `GET /weatherforecast` using `.RequireAuthorization(...)`. The policy currently requires an authenticated user; configure the authentication scheme for your application before relying on it. Without an authentication scheme, the protected endpoint will deny requests.

Customize the policy with ASP.NET Core authorization requirements, handlers, or policies, then apply it to selected routes with `.RequireAuthorization("PolicyName")`. Add `app.UseAuthentication()` before `app.UseAuthorization()` when configuring an authentication scheme. Other endpoints remain unaffected unless explicitly protected.

## Redis response caching

The API uses ASP.NET Core Output Caching with the StackExchange Redis provider. Set `ConnectionStrings:Redis` to your Redis connection string. The sample uses `localhost:6379`; start a Redis server locally or override this value for your environment. Keep production credentials in environment variables or a secret store rather than committing them to appsettings.

Each API opts in with a named policy, for example:

```csharp
app.MapGet("/weatherforecast", GetWeatherForecast)
	.CacheOutput("WeatherForecast");
```

Define that policy in `appsettings.json` under `OutputCache:Policies`. The endpoint only needs the policy name; the configured prefix and expiration apply to its cached responses:

```json
{
  "OutputCache": {
	"InstanceName": "Features:",
	"Policies": {
	  "WeatherForecast": {
		"CacheKeyPrefix": "weatherforecast:",
		"DurationSeconds": 60
	  }
	}
  }
}
```

Add one policy per API and use its name with `.CacheOutput("PolicyName")`. The prefix namespaces that policy's Redis keys; ASP.NET Core also includes the request path and relevant query variation in the output-cache key. Responses are shared across application instances using the same Redis connection and instance name. The weather endpoint remains subject to its authorization policy; configure authentication before accessing it.

## Metadata-driven data access

The data access API accepts the low-code query and command JSON contracts at `POST /api/data/query` and `POST /api/data/command`. It uses EF Core 8 with SQL Server. The engine does not call `DbContext.Set(...)` and does not create expression trees from caller-provided names. One metadata safety resolver validates entity names, scalar properties, reference navigations, keys, and child foreign keys against the active EF model. Query identifiers are then taken from the validated relational metadata and values are sent as SQL parameters. Writes use EF Core change tracking.

The sample `MetadataDataContext` maps `Order`, `Customer`, and `OrderLine` without exposing typed `DbSet<T>` properties. Replace it with the application's own mapped context and register that context, for example:

```csharp
builder.Services.AddSqlServerMetadataDataAccess<ApplicationDbContext>(builder.Configuration);
```

The context must accept `DbContextOptions<ApplicationDbContext>`. Configure `ConnectionStrings:DataAccess` for SQL Server; the sample appsettings value uses LocalDB and should be overridden for other environments. In Development, the sample creates the database on first run and seeds an order, customer, and line. In other environments, apply the application's database migrations/schema deployment separately.

#### Explicit entity exposure

Entities are not exposed merely because they appear in the EF model. Opt in each entity either with `[LowCodeExposed]` on its mapped CLR class or by listing its EF/CLR name in `MetadataDataAccess:ExposedEntities`:

```json
{
  "MetadataDataAccess": {
	"ExposedEntities": [ "Order", "Customer", "OrderLine" ]
  }
}
```

The sample uses the configuration allow-list. The registry is built once at startup from the live EF model and caches exposed entity aliases, scalar properties, and reference navigations in case-insensitive dictionaries. The allow-list and marker are additive; an empty list with no markers exposes no entities. Invalid or ambiguous configured names fail startup. Changing the model or exposure settings requires restarting the application.

`IEntityMetadataResolver` is the only low-code component that maps caller-provided names to EF metadata. The query and command engines receive it through dependency injection; they do not enumerate model entities or resolve raw property names themselves. Cached validation prevents unrecognized identifiers from becoming SQL identifiers, predicates, or write targets, and requiring explicit exposure prevents every mapped table from becoming reachable by default. Values remain parameterized. This is an input-safety boundary, not caller authorization: protect the generic endpoints with authentication and authorization policies before exposing them to untrusted clients.

Unknown or unexposed entities return HTTP 403. Unknown properties and invalid navigation paths return HTTP 400. Navigation access supports a single reference level such as `Customer.Name`; collection navigations and deeper paths are rejected.

### Query behavior

Example request:

```json
{
  "entity": "Order",
  "select": ["Id", "OrderDate", "Customer.Name", "Total"],
  "filter": {
	"logic": "AND",
	"rules": [
	  { "field": "Status", "operator": "eq", "value": "Open" },
	  {
		"logic": "OR",
		"rules": [
		  { "field": "Total", "operator": "gt", "value": 1000 },
		  { "field": "Customer.Country", "operator": "eq", "value": "IN" }
		]
	  }
	]
  },
  "sort": [{ "field": "OrderDate", "direction": "desc" }],
  "page": { "pageNumber": 1, "pageSize": 25 }
}
```

Supported operators are `eq`, `ne`, `gt`, `gte`, `lt`, `lte`, `in`, `contains`, `startsWith`, `endsWith`, `isNull`, and `isNotNull`. Filter groups accept `AND` and `OR`. Field paths can traverse mapped reference navigations, such as `Customer.Name`; collection navigations are not supported. Pages default to 25 rows and are limited to 500 rows. If sort is omitted, the primary key provides stable ordering; keyless entities must specify a sort. An omitted or empty `select` returns all root scalar properties. Entity and property names must match the EF model.

### Command behavior

Example command with a generated parent key assigned to its child:

```json
{
  "operations": [
	{
	  "entity": "Order",
	  "operationType": "Insert",
	  "clientRef": "order1",
	  "data": { "OrderDate": "2026-10-02", "Status": "Open" },
	  "children": [
		{
		  "entity": "OrderLine",
		  "operationType": "Insert",
		  "parentRef": "order1",
		  "foreignKeyProperty": "OrderId",
		  "data": { "ProductId": 101, "Qty": 2 }
		}
	  ]
	},
	{
	  "entity": "Customer",
	  "operationType": "Update",
	  "keyValues": { "Id": 42 },
	  "data": { "Email": "new@domain.com" }
	},
	{
	  "entity": "OrderLine",
	  "operationType": "Delete",
	  "keyValues": { "Id": 7 }
	}
  ]
}
```

Before opening a transaction, the engine resolves every entity, data property, key, foreign key, operation type, and payload-local reference through EF metadata. It then executes the complete batch in one transaction. For this example:

1. Insert `Order`, call `SaveChangesAsync`, and capture its generated key (for example, `Id = 501`) under the payload alias `order1`.
2. Insert `OrderLine`; resolve `parentRef: "order1"` to the in-flight order and assign its generated key to the validated `OrderId` foreign-key property before saving.
3. Load `Customer` with `FindAsync` using `Id = 42`, apply only `Email`, and save; unlisted columns remain unchanged.
4. Attach an `OrderLine` stub with `Id = 7` and remove it without selecting the full row.
5. Commit after every operation succeeds.

If any operation fails, the whole transaction is rolled back. The response has one aggregate `error` and an outcome for every operation (`Succeeded`, `Failed`, `RolledBack`, or `NotRun`); rolled-back operations are not reported as committed successes. A failed command returns HTTP 400. Nested insert operations run parent-first; nested delete operations run child-first, so callers can express dependent deletes in the operation tree. Updates and deletes require every primary key value. Update data cannot change key or concurrency-token properties. For delete targets with concurrency tokens, the engine loads the entity before removal; other deletes use a key-only stub.

### Run the low-code sample

Run the API with the `Scalar` Development profile. On first Development startup, the sample creates the LocalDB schema and inserts an example customer, order, and order line. The resolver initializes before the API begins accepting requests.

Save the query example above as `Query.json`, then send it from PowerShell:

```powershell
$query = Get-Content .\Query.json -Raw
Invoke-RestMethod -Uri 'http://localhost:5000/api/data/query' -Method Post -ContentType 'application/json' -Body $query
```

Save the command example as `Command.json` and submit it to `POST /api/data/command`:

```powershell
$command = Get-Content .\Command.json -Raw
Invoke-RestMethod -Uri 'http://localhost:5000/api/data/command' -Method Post -ContentType 'application/json' -Body $command
```

The allow-list is case-insensitive and must name entities present in the registered DbContext model. Alternatively, annotate a mapped entity class with `[LowCodeExposed]`; annotations and the configuration allow-list are additive. Use the application's own EF model for production and apply its migrations independently; the sample's Development-only `EnsureCreated` path is not a production migration strategy. An unknown or unexposed entity returns HTTP 403, while an unknown property or invalid navigation path returns HTTP 400. Configure authentication/authorization and expose only entities appropriate for each application.
