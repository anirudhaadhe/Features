using Features.Application.Events;
using Features.Application.Notifications;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;

namespace Features.Infrastructure.Notifications;

public static class NotificationServiceCollectionExtensions
{
    public static IServiceCollection AddNotificationServices(
        this IServiceCollection services,
        IConfiguration configuration)
    {
        ArgumentNullException.ThrowIfNull(services);
        ArgumentNullException.ThrowIfNull(configuration);

        services.AddOptions<NotificationOptions>()
            .Bind(configuration.GetSection(NotificationOptions.SectionName))
            .Validate(options => options.DomainEventMappings.All(mapping =>
                    !string.IsNullOrWhiteSpace(mapping.Key) &&
                    mapping.Value is not null &&
                    !string.IsNullOrWhiteSpace(mapping.Value.Category) &&
                    !string.IsNullOrWhiteSpace(mapping.Value.Message)),
                "Each domain event notification mapping requires an event type, category, and message.")
            .ValidateOnStart();

        services.TryAddScoped<INotificationService, LoggingNotificationService>();
        services.TryAddEnumerable(
            ServiceDescriptor.Scoped<IDomainEventObserver, DomainEventNotificationObserver>());

        return services;
    }
}
