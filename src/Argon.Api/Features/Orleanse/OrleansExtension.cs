namespace Argon.Api.Features;

using Argon.Api.Grains.Interfaces;
#pragma warning disable ORLEANSEXP001
using Contracts;
using Env;
using Extensions;
using Orleans.Clustering.Kubernetes;
using Orleans.Configuration;
using Orleans.Serialization;
using OrleansStreamingProviders;

public static class OrleansExtension
{
    public static WebApplicationBuilder AddOrleans(this WebApplicationBuilder builder)
    {
        builder.Services.AddSerializer(x => x.AddMemoryPackSerializer());
        builder.Host.UseOrleans(siloBuilder =>
        {
            siloBuilder
                .Configure<ClusterOptions>(builder.Configuration.GetSection("Orleans"))
                .AddStreaming()
                .UseDashboard(o => o.Port = 22832)
                .AddActivityPropagation()
                .AddAdoNetGrainStorage("OrleansStorage", options =>
                {
                    options.Invariant = "Npgsql";
                    options.ConnectionString = builder.Configuration.GetConnectionString("DefaultConnection");
                    options.GrainStorageSerializer = new MemoryPackStorageSerializer();
                });

            if (builder.Environment.IsKube())
                siloBuilder
                    .UseKubeMembership()
                    .AddActivationRepartitioner<BalanceRule>()
                    .AddPersistentStreams("default", NatsAdapterFactory.Create, options => { })
                    .AddPersistentStreams(IArgonEvent.ProviderId, NatsAdapterFactory.Create, options => { })
                    .AddRedisStorage("PubSubStore", 1)
                    .AddRedisStorage(IFusionSessionGrain.StorageId, 2);
            else
                siloBuilder
                    .UseLocalhostClustering()
                    .AddMemoryStreams("default")
                    .AddMemoryStreams(IArgonEvent.ProviderId)
                    .AddMemoryGrainStorage(IFusionSessionGrain.StorageId)
                    .AddMemoryGrainStorage("PubSubStore");
        });

        return builder;
    }
}