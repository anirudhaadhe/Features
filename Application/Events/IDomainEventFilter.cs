using Features.Domain.Events;

namespace Features.Application.Events;

public interface IDomainEventFilter
{
    ValueTask<bool> ShouldDispatchAsync(IDomainEvent domainEvent, CancellationToken cancellationToken = default);
}
