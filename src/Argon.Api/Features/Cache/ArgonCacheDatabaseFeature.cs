namespace Argon.Services;

using Features.Env;

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

        return builder.Services;
    }
}