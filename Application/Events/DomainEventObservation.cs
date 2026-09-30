using Features.Domain.Events;

namespace Features.Application.Events;

public enum DomainEventObservationKind
{
    DispatchStarted,
    DispatchFiltered,
    HandlerAttempt,
    HandlerSucceeded,
    HandlerFailed,
    DispatchCompleted,
    DispatchFailed
}

public sealed record DomainEventObservation(
    IDomainEvent DomainEvent,
    DomainEventObservationKind Kind,
    Type? HandlerType = null,
    int Attempt = 0,
    TimeSpan Elapsed = default,
    Exception? Exception = null);
