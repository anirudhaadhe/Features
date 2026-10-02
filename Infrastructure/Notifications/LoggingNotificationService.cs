using Features.Application.Notifications;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;

namespace Features.Infrastructure.Notifications;

internal sealed class LoggingNotificationService(
    ILogger<LoggingNotificationService> logger,
    IOptionsMonitor<NotificationOptions> options) : INotificationService
{
    public Task NotifyAsync(NotificationMessage notification, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(notification);
        ArgumentException.ThrowIfNullOrWhiteSpace(notification.Category);
        ArgumentException.ThrowIfNullOrWhiteSpace(notification.Message);
        cancellationToken.ThrowIfCancellationRequested();

        if (options.CurrentValue.Enabled)
        {
            logger.LogInformation(
                "Notification {NotificationCategory}: {NotificationMessage}; data: {@NotificationData}",
                notification.Category,
                notification.Message,
                notification.Data);
        }

        return Task.CompletedTask;
    }
}
