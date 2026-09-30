# Features API

A starter ASP.NET Core Minimal API targeting .NET 10.

## Run

```sh
dotnet run
```

The API exposes `GET /weatherforecast`. In Development, the OpenAPI document is available at `/openapi/v1.json`.

Use `Features.http` to send a sample request from an editor that supports HTTP files.

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
