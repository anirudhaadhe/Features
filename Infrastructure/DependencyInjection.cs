using System.Reflection;
using Features.Application.Events;
using Features.Infrastructure.Events;
using Features.Domain.Events;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;

namespace Features.Infrastructure;

public static class DependencyInjection
{
    public static IServiceCollection AddEventInfrastructure(
        this IServiceCollection services,
        IConfiguration? configuration = null,
        Action<EventInfrastructureOptions>? configure = null,
        params Assembly[] handlerAssemblies)
    {
        ArgumentNullException.ThrowIfNull(services);

        var section = configuration?.GetSection(EventInfrastructureOptions.SectionName);
        var configuredOptions = section?.Get<EventInfrastructureOptions>() ?? new EventInfrastructureOptions();
        configure?.Invoke(configuredOptions);

        var optionsBuilder = services.AddOptions<EventInfrastructureOptions>();
        if (section is not null)
        {
            optionsBuilder.Bind(section);
        }

        if (configure is not null)
        {
            optionsBuilder.Configure(configure);
        }

        optionsBuilder
            .Validate(options => options.MaxRetryAttempts is >= 0 and < int.MaxValue, "MaxRetryAttempts must be between zero and Int32.MaxValue - 1.")
            .Validate(options => options.RetryDelay >= TimeSpan.Zero, "RetryDelay cannot be negative.")
            .ValidateOnStart();

        services.TryAddScoped<IDomainEventDispatcher, InProcessDomainEventDispatcher>();

        foreach (var assemblyName in configuredOptions.HandlerAssemblyNames)
        {
            RegisterHandlersFromAssembly(services, Assembly.Load(new AssemblyName(assemblyName)));
        }

        foreach (var assembly in handlerAssemblies)
        {
            RegisterHandlersFromAssembly(services, assembly);
        }

        return services;
    }

    public static IServiceCollection AddDomainEventHandler<TDomainEvent, THandler>(this IServiceCollection services)
        where TDomainEvent : IDomainEvent
        where THandler : class, IDomainEventHandler<TDomainEvent>
    {
        services.TryAddEnumerable(
            ServiceDescriptor.Scoped(typeof(IDomainEventHandler<TDomainEvent>), typeof(THandler)));
        return services;
    }

    public static IServiceCollection AddDomainEventFilter<TFilter>(this IServiceCollection services)
        where TFilter : class, IDomainEventFilter
    {
        services.TryAddEnumerable(ServiceDescriptor.Scoped<IDomainEventFilter, TFilter>());
        return services;
    }

    public static IServiceCollection AddDomainEventObserver<TObserver>(this IServiceCollection services)
        where TObserver : class, IDomainEventObserver
    {
        services.TryAddEnumerable(ServiceDescriptor.Scoped<IDomainEventObserver, TObserver>());
        return services;
    }

    private static void RegisterHandlersFromAssembly(IServiceCollection services, Assembly assembly)
    {
        ArgumentNullException.ThrowIfNull(assembly);

        foreach (var implementationType in assembly.DefinedTypes
                     .Where(type => type is { IsAbstract: false, IsClass: true }))
        {
            foreach (var handlerInterface in implementationType.ImplementedInterfaces
                         .Where(type => type.IsGenericType &&
                                        type.GetGenericTypeDefinition() == typeof(IDomainEventHandler<>)))
            {
                services.TryAddEnumerable(
                    ServiceDescriptor.Scoped(handlerInterface, implementationType.AsType()));
            }
        }
    }
}
