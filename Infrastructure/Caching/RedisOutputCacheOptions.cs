namespace Features.Infrastructure.Caching;

public sealed class RedisOutputCacheOptions
{
    public const string SectionName = "OutputCache";

    public string InstanceName { get; set; } = "Features:";

    public Dictionary<string, ApiOutputCachePolicyOptions> Policies { get; set; } = new(StringComparer.OrdinalIgnoreCase);
}

public sealed class ApiOutputCachePolicyOptions
{
    public string CacheKeyPrefix { get; set; } = string.Empty;

    public int DurationSeconds { get; set; }

    public string? CacheTag { get; set; }
}
