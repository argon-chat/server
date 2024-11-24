namespace Argon.Api.Features;

using ActualLab.Serialization;
using Argon.Api.Features.Sentry;
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
        builder.Services.AddSerializer(x => x.AddMessagePackSerializer(null, null, MessagePackByteSerializer.Default.Options));
        builder.Host.UseOrleans(siloBuilder =>
        {
            siloBuilder.Configure<ClusterOptions>(builder.Configuration.GetSection("Orleans"))
               .AddStreaming()
               .UseDashboard(o => o.Port = 22832)
               .AddActivityPropagation()
               .AddIncomingGrainCallFilter<SentryGrainCallFilter>()
               .AddAdoNetGrainStorage("PubSubStore", options =>
                {
                    options.Invariant        = "Npgsql";
                    options.ConnectionString = builder.Configuration.GetConnectionString("DefaultConnection");
                }).AddAdoNetGrainStorage("OrleansStorage", options =>
                {
                    options.Invariant        = "Npgsql";
                    options.ConnectionString = builder.Configuration.GetConnectionString("DefaultConnection");
                });

            if (builder.Environment.IsKube())
            {
                siloBuilder
                   .UseKubeMembership()
                   .AddActivationRepartitioner<BalanceRule>()
                   .AddRedisStorage(IFusionSessionGrain.StorageId, 2)
                   .AddPersistentStreams("default", NatsAdapterFactory.Create, options => { })
                   .AddPersistentStreams(IArgonEvent.ProviderId, NatsAdapterFactory.Create, options => { });
            }
            else
            {
                siloBuilder
                   .UseLocalhostClustering()
                   .AddMemoryStreams("default")
                   .AddMemoryStreams(IArgonEvent.ProviderId)
                   .AddMemoryGrainStorage(IFusionSessionGrain.StorageId);
            }
        });

        return builder;
    }
}