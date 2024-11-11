namespace Argon.Api.Extensions;

using System.Diagnostics.CodeAnalysis;
using Features.OrleansStorageProviders;
using Microsoft.Extensions.Options;
using Orleans.Clustering.Kubernetes;
using Orleans.Configuration;
using Orleans.Placement.Repartitioning;
using Orleans.Storage;

internal class MemoryPackStorageSerializer : IGrainStorageSerializer
{
    public BinaryData Serialize<T>(T input) => new(MemoryPackSerializer.Serialize(input));

    public T Deserialize<T>(BinaryData input) => MemoryPackSerializer.Deserialize<T>(input) ?? throw new InvalidOperationException();
}

internal class Balancer : IImbalanceToleranceRule
{
    public bool IsSatisfiedBy(uint imbalance) => imbalance % 2 == 0;
}

public static class OrleansExtension
{
    public static ISiloBuilder AddRedisStorage(this ISiloBuilder builder, string providerName, Action<RedisGrainStorageOptions> options) =>
        builder.ConfigureServices(services => services.AddRedisStorage(providerName, options));

    private static IServiceCollection AddRedisStorage(this IServiceCollection services, string providerName,
        Action<RedisGrainStorageOptions> options)
    {
        services.AddOptions<RedisGrainStorageOptions>(providerName).Configure(options);

        services.AddTransient<
            IPostConfigureOptions<RedisGrainStorageOptions>, DefaultStorageProviderSerializerOptionsConfigurator<RedisGrainStorageOptions>>();

        return services.AddKeyedSingleton(providerName, RedisGrainStorageFactory.Create).AddKeyedSingleton(providerName,
            (p, n) => (ILifecycleParticipant<ISiloLifecycle>)p.GetKeyedServices<IGrainStorage>(n));
    }


    [Experimental("ORLEANSEXP001")]
    public static WebApplicationBuilder AddOrleans(this WebApplicationBuilder builder)
    {
        builder.Host.UseOrleans(siloBuilder =>
        {
            siloBuilder.Configure<ClusterOptions>(cluster =>
                {
                    cluster.ClusterId = "argonchat";
                    cluster.ServiceId = "argonchat";
                }).AddAdoNetGrainStorage("OrleansStorage", options =>
                {
                    options.Invariant              = "Npgsql";
                    options.ConnectionString       = builder.Configuration.GetConnectionString("DefaultConnection");
                    options.GrainStorageSerializer = new MemoryPackStorageSerializer();
                }).AddActivationRepartitioner<Balancer>().AddMemoryGrainStorage("CacheStorage").UseDashboard(o => o.Port = 22832)
               .AddRedisStorage("Redis", options => options.DatabaseName                                                 = 228);
            if (builder.Environment.IsDevelopment()) siloBuilder.UseLocalhostClustering();
            else siloBuilder.UseKubeMembership();
        });

        return builder;
    }
}