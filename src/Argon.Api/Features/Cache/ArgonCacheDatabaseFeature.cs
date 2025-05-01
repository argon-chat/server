namespace Argon.Services;

using Features.Env;

public static class ArgonCacheDatabaseFeature
{
    public static IServiceCollection AddArgonCacheDatabase(this WebApplicationBuilder builder)
    {
        if (builder.Environment.IsSingleInstance())
        {
            builder.Services.AddDistributedMemoryCache();
            builder.Services.AddSingleton<IArgonCacheDatabase, InMemoryArgonCacheDatabase>();
        }
        else
        {
            builder.Services.Configure<RedisConnectionPoolOptions>(builder.Configuration.GetSection("redis"));
            builder.Services.AddSingleton<IRedisPoolConnections, RedisConnectionPool>();
            builder.Services.AddHostedService(q => q.GetRequiredService<IRedisPoolConnections>());
            builder.Services.AddHostedService<RedisEventHandler>();
            builder.Services.AddSingleton<IRedisEventStorage, RedisEventStorage>();
            builder.Services.AddScoped<IArgonCacheDatabase, RedisArgonCacheDatabase>();
        }

        return builder.Services;
    }
}