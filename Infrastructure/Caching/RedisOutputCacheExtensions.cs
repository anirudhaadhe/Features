using Microsoft.AspNetCore.OutputCaching;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;

namespace Features.Infrastructure.Caching;

public static class RedisOutputCacheExtensions
{
    public static IServiceCollection AddRedisOutputCaching(
        this IServiceCollection services,
        IConfiguration configuration)
    {
        ArgumentNullException.ThrowIfNull(services);
        ArgumentNullException.ThrowIfNull(configuration);

        var redisConnection = configuration.GetConnectionString("Redis");
        if (string.IsNullOrWhiteSpace(redisConnection))
        {
            throw new InvalidOperationException("Configure ConnectionStrings:Redis before enabling Redis output caching.");
        }

        var options = configuration.GetSection(RedisOutputCacheOptions.SectionName).Get<RedisOutputCacheOptions>()
            ?? throw new InvalidOperationException("Configure the OutputCache section before enabling Redis output caching.");

        if (string.IsNullOrWhiteSpace(options.InstanceName))
        {
            throw new InvalidOperationException("OutputCache:InstanceName cannot be empty.");
        }

        if (options.Policies.Count == 0)
        {
            throw new InvalidOperationException("Configure at least one named policy under OutputCache:Policies.");
        }

        foreach (var (policyName, policy) in options.Policies)
        {
            if (string.IsNullOrWhiteSpace(policyName))
            {
                throw new InvalidOperationException("Output cache policy names cannot be empty.");
            }

            if (string.IsNullOrWhiteSpace(policy.CacheKeyPrefix))
            {
                throw new InvalidOperationException($"Output cache policy '{policyName}' must configure a CacheKeyPrefix.");
            }

            if (policy.DurationSeconds <= 0)
            {
                throw new InvalidOperationException($"Output cache policy '{policyName}' must have a positive DurationSeconds value.");
            }
        }

        services.AddStackExchangeRedisOutputCache(redisOptions =>
        {
            redisOptions.Configuration = redisConnection;
            redisOptions.InstanceName = options.InstanceName;
        });

        services.AddOutputCache(outputCacheOptions =>
        {
            foreach (var (policyName, policy) in options.Policies)
            {
                outputCacheOptions.AddPolicy(policyName, policyBuilder =>
                {
                    policyBuilder
                        .Expire(TimeSpan.FromSeconds(policy.DurationSeconds))
                        .SetCacheKeyPrefix(policy.CacheKeyPrefix);

                    if (!string.IsNullOrWhiteSpace(policy.CacheTag))
                    {
                        policyBuilder.Tag(policy.CacheTag);
                    }
                });
            }
        });

        return services;
    }
}
