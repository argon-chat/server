namespace Argon.Features;

using ActualLab.Serialization;
using Env;
using MessagePack.Formatters;
using MessagePack.Resolvers;
using Orleans.Clustering.Kubernetes;
using Orleans.Configuration;
using Orleans.Providers;
using Orleans.Serialization;
using OrleansStreamingProviders;
using Sentry;
using Shared;

#pragma warning disable ORLEANSEXP001

public static class OrleansExtension
{
    public static WebApplicationBuilder AddOrleans(this WebApplicationBuilder builder)
    {
        builder.Services.AddSerializer(x => x.AddMessagePackSerializer(null, null, MessagePackSerializer.DefaultOptions));
        builder.Host.UseOrleans(siloBuilder =>
        {
            siloBuilder.Configure<ClusterOptions>(builder.Configuration.GetSection("Orleans"))
               .AddStreaming()
               .UseDashboard(o => o.Port = 22832)
               .AddActivityPropagation()
               .AddIncomingGrainCallFilter<SentryGrainCallFilter>()
               .AddAdoNetGrainStorage(ProviderConstants.DEFAULT_PUBSUB_PROVIDER_NAME, options =>
                {
                    options.Invariant        = "Npgsql";
                    options.ConnectionString = builder.Configuration.GetConnectionString("DefaultConnection");
                }).AddAdoNetGrainStorage(ProviderConstants.DEFAULT_STORAGE_PROVIDER_NAME, x =>
                {
                    x.Invariant        = "Npgsql";
                    x.ConnectionString = builder.Configuration.GetConnectionString("DefaultConnection");
                })
               .AddAdoNetGrainStorage(IFusionSessionGrain.StorageId, x => {
                    x.Invariant        = "Npgsql";
                    x.ConnectionString = builder.Configuration.GetConnectionString("DefaultConnection");
                });

            if (builder.Environment.IsKube())
            {
                //siloBuilder
                //   .UseKubeMembership()
                //   .AddActivationRepartitioner<BalanceRule>()
                //   .AddRedisStorage(IFusionSessionGrain.StorageId, 2)
                //   .AddPersistentStreams("default", NatsAdapterFactory.Create, options => { })
                //   .AddPersistentStreams(IArgonEvent.ProviderId, NatsAdapterFactory.Create, options => { })
                //   .AddBroadcastChannel(IArgonEvent.Broadcast);
            }
            else
            {
                
            }

            siloBuilder
               .UseKubeMembership()
               .AddPersistentStreams("default", NatsAdapterFactory.Create, options => { })
               .AddPersistentStreams(IArgonEvent.ProviderId, NatsAdapterFactory.Create, options => { })
               .AddBroadcastChannel(IArgonEvent.Broadcast);
        });

        return builder;
    }
}