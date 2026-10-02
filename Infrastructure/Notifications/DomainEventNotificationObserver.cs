using Features.Application.Events;
using Features.Application.Notifications;
using Microsoft.Extensions.Options;

namespace Features.Infrastructure.Notifications;

internal sealed class DomainEventNotificationObserver(
    INotificationService notificationService,
    IOptionsMonitor<NotificationOptions> options) : IDomainEventObserver
{
    public async ValueTask ObserveAsync(DomainEventObservation observation, CancellationToken cancellationToken = default)
    {
        var currentOptions = options.CurrentValue;
        if (!currentOptions.Enabled || observation.Kind != DomainEventObservationKind.DispatchCompleted)
        {
            return;
        }

        var domainEvent = observation.DomainEvent;
        var eventType = domainEvent.GetType();
        var eventTypeName = eventType.FullName ?? eventType.Name;
        if (!currentOptions.DomainEventMappings.TryGetValue(eventTypeName, out var mapping))
        {
            return;
        }

        await notificationService.NotifyAsync(
            new NotificationMessage(
                mapping.Category,
                mapping.Message,
                new
                {
                    EventType = eventTypeName,
                    Event = domainEvent,
                    domainEvent.OccurredOnUtc
                }),
            cancellationToken);
    }
}
