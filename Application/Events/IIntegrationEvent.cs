namespace Features.Application.Events;

public interface IIntegrationEvent
{
    DateTimeOffset OccurredOnUtc { get; }
}
