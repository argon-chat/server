namespace Argon.Api.Features.Orleans;

using Argon.Api.Features.OrleansStorageProviders;
using global::Orleans.Configuration;
using global::Orleans.Runtime.Hosting;
using global::Orleans.Storage;
using Microsoft.Extensions.Options;

public static class RedisExtensions
{
    public static ISiloBuilder AddRedisStorage(this ISiloBuilder builder, string providerName, Action<RedisGrainStorageOptions> options) =>
        builder.ConfigureServices(services => services.AddRedisStorage(providerName, options));

    public static IServiceCollection AddRedisStorage(this IServiceCollection services, string providerName, Action<RedisGrainStorageOptions> options)
    {
        services.AddOptions<RedisGrainStorageOptions>(providerName).Configure(options);
        services.ConfigureNamedOptionForLogging<RedisGrainStorageOptions>(providerName);

        services.AddTransient<
            IPostConfigureOptions<RedisGrainStorageOptions>, DefaultStorageProviderSerializerOptionsConfigurator<RedisGrainStorageOptions>>();

        return services.AddGrainStorage<IGrainStorage>(providerName, RedisGrainStorageFactory.Create);
    }
}