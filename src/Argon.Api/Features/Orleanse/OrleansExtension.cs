namespace Argon.Api.Features;

using Contracts;
using Env;
using Extensions;
using Grains.Interfaces;
using Orleans.Clustering.Kubernetes;
using Orleans.Configuration;
using Orleans.Serialization;
using OrleansStreamingProviders;

#pragma warning disable ORLEANSEXP001

public static class OrleansExtension
{
    public static WebApplicationBuilder AddOrleans(this WebApplicationBuilder builder)
    {
        builder.Services.AddSerializer(x => x.AddMemoryPackSerializer());
        builder.Host.UseOrleans(siloBuilder =>
        {
            siloBuilder.Configure<ClusterOptions>(builder.Configuration.GetSection("Orleans")).AddStreaming().UseDashboard(o => o.Port = 22832)
               .AddActivityPropagation().AddAdoNetGrainStorage("PubSubStore", options =>
                {
                    options.Invariant              = "Npgsql";
                    options.ConnectionString       = builder.Configuration.GetConnectionString("DefaultConnection");
                    options.GrainStorageSerializer = new MemoryPackStorageSerializer();
                }).AddAdoNetGrainStorage("OrleansStorage", options =>
                {
                    options.Invariant              = "Npgsql";
                    options.ConnectionString       = builder.Configuration.GetConnectionString("DefaultConnection");
                    options.GrainStorageSerializer = new MemoryPackStorageSerializer();
                });

            if (builder.Environment.IsKube())
            {
                siloBuilder.UseKubeMembership().AddActivationRepartitioner<BalanceRule>().AddRedisStorage(IFusionSessionGrain.StorageId, 2)
                   .AddPersistentStreams("default", NatsAdapterFactory.Create, options => { })
                   .AddPersistentStreams(IArgonEvent.ProviderId, NatsAdapterFactory.Create, options => { });
            }
            else
            {
                siloBuilder.UseLocalhostClustering().AddPersistentStreams("default", NatsAdapterFactory.Create, options => { })
                   .AddPersistentStreams(IArgonEvent.ProviderId, NatsAdapterFactory.Create, options => { })
                    // .AddMemoryStreams("default").AddMemoryStreams(IArgonEvent.ProviderId)
                   .AddRedisStorage(IFusionSessionGrain.StorageId, 1).AddRedisStorage("PubSubStore", 2);
            }
        });

        return builder;
    }
}