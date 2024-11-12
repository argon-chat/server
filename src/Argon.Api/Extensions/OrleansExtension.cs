namespace Argon.Api.Extensions;

using System.Diagnostics.CodeAnalysis;
using Features.OrleansStorageProviders;
using Microsoft.Extensions.Options;
using Orleans.Clustering.Kubernetes;
using Orleans.Configuration;
using Orleans.Placement.Repartitioning;
using Orleans.Providers.Streams.Generator;
using Orleans.Runtime.Hosting;
using Orleans.Storage;
using Orleans.Streams;

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
    private static ISiloBuilder AddRedisStorage(this ISiloBuilder builder, string providerName, Action<RedisGrainStorageOptions> options) =>
        builder.ConfigureServices(services => services.AddRedisStorage(providerName, options));

    private static IServiceCollection AddRedisStorage(this IServiceCollection services, string providerName,
        Action<RedisGrainStorageOptions> options)
    {
        services.AddOptions<RedisGrainStorageOptions>(providerName).Configure(options);
        services.ConfigureNamedOptionForLogging<RedisGrainStorageOptions>(providerName);

        services.AddTransient<
            IPostConfigureOptions<RedisGrainStorageOptions>, DefaultStorageProviderSerializerOptionsConfigurator<RedisGrainStorageOptions>>();

        return services.AddGrainStorage(providerName, RedisGrainStorageFactory.Create);
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
                    options.Invariant = "Npgsql";
                    options.ConnectionString = builder.Configuration.GetConnectionString("DefaultConnection");
                    options.GrainStorageSerializer = new MemoryPackStorageSerializer();
                }).AddActivationRepartitioner<Balancer>().AddMemoryGrainStorage("CacheStorage").UseDashboard(o => o.Port = 22832)
               .AddRedisStorage("PubSubStore", options => options.DatabaseName = 0).AddStreaming().AddPersistentStreams( // TODO
                    "TestProvider", GeneratorAdapterFactory.Create, b =>
                    {
                        b.ConfigurePullingAgent(ob => ob.Configure(options => options.BatchContainerBatchSize               = 15));
                        b.Configure<HashRingStreamQueueMapperOptions>(ob => ob.Configure(options => options.TotalQueueCount = 16));
                        b.UseConsistentRingQueueBalancer();
                        b.ConfigureStreamPubSub(StreamPubSubType.ExplicitGrainBasedOnly);
                    });
            if (builder.Environment.IsDevelopment()) siloBuilder.UseLocalhostClustering();
            else siloBuilder.UseKubeMembership();
        });

        return builder;
    }
}