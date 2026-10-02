namespace Features.Infrastructure.Notifications;

public sealed class NotificationOptions
{
    public const string SectionName = "Notifications";

    public bool Enabled { get; set; } = true;

    public Dictionary<string, DomainEventNotificationOptions> DomainEventMappings { get; set; } = new(StringComparer.OrdinalIgnoreCase);
}

public sealed class DomainEventNotificationOptions
{
    public string Category { get; set; } = string.Empty;

    public string Message { get; set; } = string.Empty;
}
