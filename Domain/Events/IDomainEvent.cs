namespace Features.Domain.Events;

public interface IDomainEvent
{
    DateTimeOffset OccurredOnUtc { get; }
}

public abstract record DomainEvent : IDomainEvent
{
    public DateTimeOffset OccurredOnUtc { get; } = DateTimeOffset.UtcNow;
}
