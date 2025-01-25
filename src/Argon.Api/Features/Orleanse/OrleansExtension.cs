namespace Argon.Features;

using Env;
using Orleans.Clustering.Kubernetes;
using Orleans.Configuration;
using Orleans.Hosting;
using Orleans.Providers;
using OrleansStreamingProviders;
using Sentry;

#pragma warning disable ORLEANSEXP001

public static class OrleansExtension
{
    public static WebApplicationBuilder AddOrleans(this WebApplicationBuilder builder)
    {
        builder.Host.UseOrleans(siloBuilder =>
        {
            siloBuilder.Configure<ClusterOptions>(builder.Configuration.GetSection("Orleans"))
               .AddStreaming()
               .AddActivityPropagation()
               .AddReminders()
               .UseDashboard(o => o.Port = 22832)
               .AddIncomingGrainCallFilter<SentryGrainCallFilter>()
               .UseStorages([
                    ProviderConstants.DEFAULT_PUBSUB_PROVIDER_NAME,
                    ProviderConstants.DEFAULT_STORAGE_PROVIDER_NAME,
                    IFusionSessionGrain.StorageId,
                    IServerInvitesGrain.StorageId,
                ], "Npgsql", "DefaultConnection")
               .UseAdoNetReminderService(x =>
                {
                    x.Invariant        = "Npgsql";
                    x.ConnectionString = builder.Configuration.GetConnectionString("DefaultConnection");
                });

            if (builder.Environment.IsKube())
            {
                siloBuilder
                   .AddActivationRepartitioner<BalanceRule>()
                   .UseKubernetesHosting()
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


    public static ISiloBuilder UseStorages(this ISiloBuilder builder, List<string> keys, string invariant, string connString)
    {
        foreach (var key in keys)
        {
            builder.AddAdoNetGrainStorage(key, x =>
            {
                x.Invariant        = invariant;
                x.ConnectionString = builder.Configuration.GetConnectionString(connString);
            });
        }

        return builder;
    }
}