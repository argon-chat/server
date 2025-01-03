namespace Argon.Features;

using Env;
using Orleans.Clustering.Kubernetes;
using Orleans.Configuration;
using Orleans.Hosting;
using Orleans.Providers;
using Orleans.Serialization;
using OrleansStreamingProviders;
using Sentry;

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
               .AddActivityPropagation()
               .AddReminders()
               .UseDashboard(o => o.Port = 22832)
               .AddIncomingGrainCallFilter<SentryGrainCallFilter>()
               .AddAdoNetGrainStorage(ProviderConstants.DEFAULT_PUBSUB_PROVIDER_NAME, options =>
                {
                    options.Invariant        = "Npgsql";
                    options.ConnectionString = builder.Configuration.GetConnectionString("DefaultConnection");
                })
               .AddAdoNetGrainStorage(ProviderConstants.DEFAULT_STORAGE_PROVIDER_NAME, x =>
                {
                    x.Invariant        = "Npgsql";
                    x.ConnectionString = builder.Configuration.GetConnectionString("DefaultConnection");
                })
               .AddAdoNetGrainStorage(IFusionSessionGrain.StorageId, x =>
                {
                    x.Invariant        = "Npgsql";
                    x.ConnectionString = builder.Configuration.GetConnectionString("DefaultConnection");
                })
               .UseAdoNetReminderService(x =>
                {
                    x.Invariant        = "Npgsql";
                    x.ConnectionString = builder.Configuration.GetConnectionString("DefaultConnection");
                });

            if (builder.Environment.IsKube())
            {
                siloBuilder
                   .AddActivationRepartitioner<BalanceRule>()
                   .UseKubeMembership()
                   .AddPersistentStreams("default", NatsAdapterFactory.Create, options => { })
                   .AddPersistentStreams(IArgonEvent.ProviderId, NatsAdapterFactory.Create, options => { })
                   .AddBroadcastChannel(IArgonEvent.Broadcast);
                //siloBuilder
                //   .UseKubeMembership()
                //   .AddActivationRepartitioner<BalanceRule>()
                //   .AddRedisStorage(IFusionSessionGrain.StorageId, 2)
                //   .AddPersistentStreams("default", NatsAdapterFactory.Create, options => { })
                //   .AddPersistentStreams(IArgonEvent.ProviderId, NatsAdapterFactory.Create, options => { })
                //   .AddBroadcastChannel(IArgonEvent.Broadcast);
            }
            else
                siloBuilder
                   .UseLocalhostClustering()
                   .AddMemoryStreams("default")
                   .AddMemoryStreams(IArgonEvent.ProviderId)
                   .AddBroadcastChannel(IArgonEvent.Broadcast);
        });

        return builder;
    }
}