namespace Argon.Features;

using Env;
using Orleans.Configuration;
using Orleans.Hosting;
using Orleans.Providers;
using Orleans.Streams;
using OrleansStreamingProviders.V2;
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
                siloBuilder
                   .AddActivationRepartitioner<BalanceRule>()
                    //.AddEventHubStreams("default", (configurator) =>
                    // {
                    //     configurator.ConfigureEventHub(q => q.Configure(options =>
                    //     {
                    //         options.ConfigureEventHubConnection(
                    //             builder.Configuration.GetConnectionString("azure-hub"),
                    //             "default",
                    //             "default");
                    //     }));
                    //     configurator.UseAzureTableCheckpointer(
                    //         e => e.Configure(options =>
                    //         {
                    //             options.ConfigureTableServiceClient(builder.Configuration.GetConnectionString("azure-hub"));
                    //             options.PersistInterval = TimeSpan.FromSeconds(10);
                    //         }));
                    // })
                    //.AddEventHubStreams(IArgonEvent.ProviderId, (configurator) => {
                    //     configurator.ConfigureEventHub(q => q.Configure(options => {
                    //         options.ConfigureEventHubConnection(
                    //             builder.Configuration.GetConnectionString("azure-hub"),
                    //             IArgonEvent.ProviderId,
                    //             IArgonEvent.ProviderId);
                    //     }));
                    //     configurator.UseAzureTableCheckpointer(
                    //         e => e.Configure(options => {
                    //             options.ConfigureTableServiceClient(builder.Configuration.GetConnectionString("azure-hub"));
                    //             options.PersistInterval = TimeSpan.FromSeconds(10);
                    //         }));
                    // })
                   .AddAdoNetStreams("default", (AdoNetStreamOptions x) =>
                    {
                        x.Invariant        = "Npgsql";
                        x.ConnectionString = builder.Configuration.GetConnectionString("DefaultConnection");
                        x.MaxAttempts      = 1;
                    })
                   .AddAdoNetStreams(IArgonEvent.ProviderId, x =>
                    {
                        x.Invariant        = "Npgsql";
                        x.ConnectionString = builder.Configuration.GetConnectionString("DefaultConnection");
                        x.MaxAttempts      = 1;
                    })
                    //.AddNatsStreams("default", x =>
                    // {
                    //     x.ConnectionString = builder.Configuration.GetConnectionString("nats")!;
                    // })
                    //.AddNatsStreams(IArgonEvent.ProviderId, x => {
                    //     x.ConnectionString = builder.Configuration.GetConnectionString("nats")!;
                    // })

                    //.AddPersistentStreams("default", NatsAdapterFactory.Create, _ => { })
                    //.AddPersistentStreams(IArgonEvent.ProviderId, NatsAdapterFactory.Create, _ => { })
                   .UseAdoNetClustering(x =>
                    {
                        x.Invariant        = "Npgsql";
                        x.ConnectionString = builder.Configuration.GetConnectionString("DefaultConnection");
                    })
                   .AddBroadcastChannel(IArgonEvent.Broadcast);
            else
                siloBuilder
                   .UseLocalhostClustering()
                   .AddMemoryStreams(IArgonEvent.ProviderId)
                   .AddMemoryStreams("default")
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