namespace Argon.Extensions;

using Features.Orleanse.Storages;
using Orleans.Configuration;
using Orleans.Runtime.Hosting;
using Orleans.Storage;

public static class RedisExtensions
{
    public static ISiloBuilder AddRedisStorage(this ISiloBuilder builder, string providerName, Action<RedisGrainStorageOptions> options) =>
        builder.ConfigureServices(services => services.AddRedisStorage(providerName, options));
    // The Redis database is taken from the OrleansStorage profile, not per-provider, so no db arg here.
    public static ISiloBuilder AddRedisStorage(this ISiloBuilder builder, string providerName) =>
        builder.AddRedisStorage(providerName, static _ => { });

    public static IServiceCollection AddRedisStorage(this IServiceCollection services, string providerName,
        Action<RedisGrainStorageOptions> options)
    {
        services.AddOptions<RedisGrainStorageOptions>(providerName).Configure(options);
        services.ConfigureNamedOptionForLogging<RedisGrainStorageOptions>(providerName);

        services.AddTransient<
            IPostConfigureOptions<RedisGrainStorageOptions>, DefaultStorageProviderSerializerOptionsConfigurator<RedisGrainStorageOptions>>();

        return services.AddGrainStorage(providerName, RedisGrainStorageFactory.Create);
    }
}