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

The API exposes `GET /weatherforecast` and `GET /api/products`.

Use `Features.http` to send a sample request from an editor that supports HTTP files.

### Sample products API

`GET /api/products` returns 100 prepopulated sample products from `Data/products.json`. The JSON file is copied to build and publish output. This endpoint uses the `Products` Redis output-cache policy configured under `OutputCache:Policies` in `appsettings.json`.

The catalog loads the fixture into process memory at startup. In Development, `POST /api/products/seed` reloads the fixture into memory and invalidates cached product responses. The in-memory catalog resets to the fixture whenever the application restarts and is not shared between app instances; Redis stores only cached HTTP responses, not the product data.

Run the application in Development and open `http://localhost:5000/scalar/v1` to browse and try the API. The generated OpenAPI document is at `http://localhost:5000/openapi/v1.json`.

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
