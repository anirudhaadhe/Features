using Features.Application.DataAccess;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;

namespace Features.Infrastructure.DataAccess;

public static class MetadataDataAccessServiceCollectionExtensions
{
    public static IServiceCollection AddMetadataDataAccess<TContext>(
        this IServiceCollection services,
        Action<IServiceProvider, DbContextOptionsBuilder> configureOptions,
        Action<MetadataDataAccessOptions>? configureResolverOptions = null)
        where TContext : DbContext
    {
        ArgumentNullException.ThrowIfNull(services);
        ArgumentNullException.ThrowIfNull(configureOptions);

        services.AddDbContext<TContext>(configureOptions);
        if (configureResolverOptions is not null)
        {
            services.Configure(configureResolverOptions);
        }

        services.TryAddScoped<DbContext>(serviceProvider => serviceProvider.GetRequiredService<TContext>());
        services.TryAddSingleton<IEntityMetadataResolver>(serviceProvider =>
        {
            using var scope = serviceProvider.CreateScope();
            var context = scope.ServiceProvider.GetRequiredService<TContext>();
            var options = serviceProvider.GetRequiredService<Microsoft.Extensions.Options.IOptions<MetadataDataAccessOptions>>();
            return new EntityMetadataResolver(context.Model, options);
        });
        services.TryAddScoped<IMetadataDataEngine, MetadataDataEngine>();
        services.TryAddScoped<ICommandService>(serviceProvider =>
            serviceProvider.GetRequiredService<IMetadataDataEngine>() as ICommandService
            ?? throw new InvalidOperationException("The metadata data engine does not provide command execution."));
        return services;
    }

    public static IServiceCollection AddSqlServerMetadataDataAccess<TContext>(
        this IServiceCollection services,
        IConfiguration configuration,
        string connectionStringName = "DataAccess")
        where TContext : DbContext
    {
        ArgumentNullException.ThrowIfNull(configuration);
        var connectionString = configuration.GetConnectionString(connectionStringName);
        if (string.IsNullOrWhiteSpace(connectionString))
        {
            throw new InvalidOperationException(
                $"Connection string '{connectionStringName}' is not configured.");
        }

        return services.AddMetadataDataAccess<TContext>(
            (_, options) => options.UseSqlServer(connectionString),
            metadataOptions => configuration.GetSection(MetadataDataAccessOptions.SectionName).Bind(metadataOptions));
    }
}
