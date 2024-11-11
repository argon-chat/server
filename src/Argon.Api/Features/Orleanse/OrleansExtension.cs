namespace Argon.Api.Features;

using System.Diagnostics.CodeAnalysis;
using IdentityModel.Client;
using Orleans.Clustering.Kubernetes;
using Orleans.Configuration;
using Orleans.Providers.Streams.Generator;
using Orleans.Storage;
using Orleans.Streams;

internal class MemoryPackStorageSerializer : IGrainStorageSerializer
{
    public BinaryData Serialize<T>(T input) => new(MemoryPackSerializer.Serialize(input));

    public T Deserialize<T>(BinaryData input) => MemoryPackSerializer.Deserialize<T>(input) ?? throw new InvalidOperationException();
}

public static class OrleansExtension
{
    [Experimental("ORLEANSEXP001")]
    public static WebApplicationBuilder AddOrleans(this WebApplicationBuilder builder)
    {
        builder.Host.UseOrleans(siloBuilder =>
        {
            siloBuilder.Configure<ClusterOptions>(cluster =>
                {
                    cluster.ClusterId = "argonchat";
                    cluster.ServiceId = "argonchat";
                })
               .AddAdoNetGrainStorage("OrleansStorage", options =>
                {
                    options.Invariant              = "Npgsql";
                    options.ConnectionString       = builder.Configuration.GetConnectionString("DefaultConnection");
                    options.GrainStorageSerializer = new MemoryPackStorageSerializer();
                })
               .AddActivationRepartitioner<BalanceRule>()
               .AddPersistentStreams(
                    "default",
                    GeneratorAdapterFactory.Create,
                    b =>
                    {
                        b.ConfigurePullingAgent(ob => ob.Configure(options => options.BatchContainerBatchSize = 15));
                        b.Configure<HashRingStreamQueueMapperOptions>(ob
                            => ob.Configure(options => options.TotalQueueCount = 16));
                        b.UseConsistentRingQueueBalancer();
                        b.ConfigureStreamPubSub();
                    })
               .AddMemoryGrainStorage("CacheStorage")
               .UseDashboard(o => o.Port = 22832);
            if (builder.Environment.IsDevelopment())
                siloBuilder.UseLocalhostClustering();
            else
                siloBuilder.UseKubeMembership();
        });

        return builder;
    }
}