namespace Argon.Services;

using L1L2;
using Microsoft.Extensions.Caching.Distributed;

public static class ArgonCacheDatabaseFeature
{
    public static IServiceCollection AddArgonCacheDatabase(this WebApplicationBuilder builder)
    {
        var registry = new RedisProfileRegistry(builder.Configuration);
        builder.Services.AddSingleton(registry);

        // One pool per pooled profile, each with its own connection string + default database.
        // Validated eagerly: Resolve throws here if a profile is missing its connection string.
        foreach (var name in RedisProfiles.Pooled)
        {
            var profileName = name;
            var profile     = registry.Resolve(profileName);
            var maxSize     = registry.MaxSizeOf(profileName);

            builder.Services.AddKeyedSingleton<IRedisPoolConnections>(profileName, (sp, _) =>
                new RedisConnectionPool(profileName, profile, maxSize, sp.GetRequiredService<ILogger<RedisConnectionPool>>()));
        }

        builder.Services.AddHostedService<RedisPoolCoordinator>();

        builder.Services.AddSingleton<IArgonCacheDatabase, RedisArgonCacheDatabase>();
        builder.Services.AddSingleton<IDistributedCache, RedisDistributedCache>();
        builder.AddHybridCache();

        return builder.Services;
    }
}

/// <summary>
/// Starts/stops the keyed <see cref="RedisConnectionPool"/> instances. Keyed singletons can't be
/// registered as hosted services directly, so this coordinator owns their lifecycle.
/// </summary>
internal sealed class RedisPoolCoordinator(IServiceProvider provider) : IHostedService
{
    private readonly List<IRedisPoolConnections> pools = new();

    public async Task StartAsync(CancellationToken cancellationToken)
    {
        foreach (var name in RedisProfiles.Pooled)
        {
            var pool = provider.GetRequiredKeyedService<IRedisPoolConnections>(name);
            pools.Add(pool);
            await pool.StartAsync(cancellationToken);
        }
    }

    public async Task StopAsync(CancellationToken cancellationToken)
    {
        foreach (var pool in pools)
            await pool.StopAsync(cancellationToken);
    }
}
