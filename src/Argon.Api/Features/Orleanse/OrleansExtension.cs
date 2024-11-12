namespace Argon.Api.Features;

using Contracts;
using Orleans.Clustering.Kubernetes;
using Orleans.Configuration;
using Orleans.Providers.Streams.Generator;
using Orleans.Streams;

public static class OrleansExtension
{
    public static WebApplicationBuilder AddOrleans(this WebApplicationBuilder builder)
    {
        builder.Host.UseOrleans(siloBuilder =>
        {
        #pragma warning disable ORLEANSEXP001
            siloBuilder.Configure<ClusterOptions>(cluster =>
                {
                    cluster.ClusterId = "argonchat";
                    cluster.ServiceId = "argonchat";
                })
               .AddMemoryGrainStorage("PubSubStore")
               .AddAdoNetGrainStorage("OrleansStorage", options =>
                {
                    options.Invariant              = "Npgsql";
                    options.ConnectionString       = builder.Configuration.GetConnectionString("DefaultConnection");
                    options.GrainStorageSerializer = new MemoryPackStorageSerializer();
                })
               .AddActivationRepartitioner<BalanceRule>()
            #pragma warning restore ORLEANSEXP001
               .AddPersistentStreams( // TODO
                    "default",
                    GeneratorAdapterFactory.Create,
                    b =>
                    {
                        b.ConfigurePullingAgent(ob => ob.Configure(options => options.BatchContainerBatchSize = 15));
                        b.Configure<HashRingStreamQueueMapperOptions>(ob
                            => ob.Configure(options => options.TotalQueueCount = 16));
                        b.UseConsistentRingQueueBalancer();
                        b.ConfigureStreamPubSub(StreamPubSubType.ExplicitGrainBasedOnly);
                    })
               .AddPersistentStreams( // TODO
                    IArgonEvent.ProviderId,
                    GeneratorAdapterFactory.Create,
                    b => {
                        b.ConfigurePullingAgent(ob => ob.Configure(options => options.BatchContainerBatchSize = 15));
                        b.Configure<HashRingStreamQueueMapperOptions>(ob
                            => ob.Configure(options => options.TotalQueueCount = 16));
                        b.UseConsistentRingQueueBalancer();
                        b.ConfigureStreamPubSub(StreamPubSubType.ExplicitGrainBasedOnly);
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