namespace Argon.Api.Extensions;

using Argon.Api.Features.Orleanse.Storages;
using Features.OrleansStorageProviders;
using Microsoft.Extensions.Options;
using Orleans.Configuration;
using Orleans.Runtime.Hosting;
using Orleans.Storage;

public static class RedisExtensions
{
    public static ISiloBuilder AddRedisStorage(this ISiloBuilder builder, string providerName, Action<RedisGrainStorageOptions> options) =>
        builder.ConfigureServices(services => services.AddRedisStorage(providerName, options));
    public static ISiloBuilder AddRedisStorage(this ISiloBuilder builder, string providerName, int indexDb) =>
        builder.ConfigureServices(services => services.AddRedisStorage(providerName, options => options.DatabaseName = indexDb));

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