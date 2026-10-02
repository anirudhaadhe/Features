namespace Features.Application.Notifications;

public sealed record NotificationMessage(
    string Category,
    string Message,
    object? Data = null);
