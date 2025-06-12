namespace Argon.Services;

using Features.Env;
using L1L2;
using Microsoft.Extensions.Caching.Distributed;

public static class ArgonCacheDatabaseFeature
{
    public static IServiceCollection AddArgonCacheDatabase(this WebApplicationBuilder builder)
    {
        builder.Services.Configure<RedisConnectionPoolOptions>(builder.Configuration.GetSection("redis"));
        builder.Services.AddSingleton<IRedisPoolConnections, RedisConnectionPool>();
        builder.Services.AddHostedService(q => q.GetRequiredService<IRedisPoolConnections>());
        builder.Services.AddHostedService<RedisEventHandler>();
        builder.Services.AddSingleton<IRedisEventStorage, RedisEventStorage>();
        builder.Services.AddSingleton<IArgonCacheDatabase, RedisArgonCacheDatabase>();
        builder.Services.Configure<RedisDistributedCacheOptions>(builder.Configuration.GetSection("redis:l2"));
        builder.Services.AddSingleton<IDistributedCache, RedisDistributedCache>();
        builder.AddHybridCache();
        
        return builder.Services;
    }
}