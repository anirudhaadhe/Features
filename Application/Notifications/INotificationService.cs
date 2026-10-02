namespace Features.Application.Notifications;

public interface INotificationService
{
    Task NotifyAsync(NotificationMessage notification, CancellationToken cancellationToken = default);
}
