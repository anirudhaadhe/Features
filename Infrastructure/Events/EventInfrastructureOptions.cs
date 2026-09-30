namespace Features.Infrastructure.Events;

public sealed class EventInfrastructureOptions
{
    public const string SectionName = "EventInfrastructure";

    public List<string> HandlerAssemblyNames { get; set; } = [];

    public int MaxRetryAttempts { get; set; }

    public TimeSpan RetryDelay { get; set; } = TimeSpan.Zero;
}
