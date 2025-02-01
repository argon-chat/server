namespace Argon.Features;

using Api.Features.Orleans.Consul;
using Env;
using Orleans.Configuration;
using Orleans.Hosting;
using Orleans.Providers;
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
                })
               .Configure<ClusterMembershipOptions>(options =>
                {
                    options.IAmAliveTablePublishTimeout = TimeSpan.FromSeconds(10);
                    options.LivenessEnabled             = false;
                });

            if (builder.Environment.IsKube())
                siloBuilder
                   //.AddActivationRepartitioner<BalanceRule>()
                   .AddConsulGrainDirectory("servers")
                   .AddConsulGrainDirectory("channels")
                   .AddConsulClustering()
                   .AddAdoNetStreams("default", x =>
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
                   .AddBroadcastChannel(IArgonEvent.Broadcast);
            else
                siloBuilder
                   .AddConsulClustering()
                   .AddConsulGrainDirectory("servers")
                   .AddConsulGrainDirectory("channels")
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