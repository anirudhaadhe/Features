namespace Features.Application.Events;

public interface IDomainEventObserver
{
    ValueTask ObserveAsync(DomainEventObservation observation, CancellationToken cancellationToken = default);
}
