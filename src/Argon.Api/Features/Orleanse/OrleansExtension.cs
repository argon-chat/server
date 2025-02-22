namespace Argon.Features;

using Api.Features;
using Api.Features.Orleans.Consul;
using Env;
using Orleans.Configuration;
using Orleans.Hosting;
using Orleans.Providers;
using Sentry;
using Api.Features.Orleans.Streams.Nats;

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
                    IUserSessionGrain.StorageId,
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
                })
               .Configure<GrainCollectionOptions>(options =>
                {
                    options.CollectionAge     = TimeSpan.FromMinutes(4);
                    options.CollectionQuantum = TimeSpan.FromMinutes(2);
                })
               .Configure<SchedulingOptions>(options =>
                {
                    options.StoppedActivationWarningInterval = TimeSpan.FromHours(1);
                    options.TurnWarningLengthThreshold       = TimeSpan.FromSeconds(10);
                });

            if (builder.Environment.IsKube())
                siloBuilder
                    //.AddActivationRepartitioner<BalanceRule>()
                   .AddConsulGrainDirectory("servers")
                   .AddConsulGrainDirectory("channels")
                   .AddConsulClustering()
                   .AddNatsStreaming("default")
                   .AddNatsStreaming(IArgonEvent.ProviderId)
                   .AddBroadcastChannel(IArgonEvent.Broadcast);
            else
                siloBuilder
                   .AddInMemoryGrainDirectory("servers")
                   .AddInMemoryGrainDirectory("channels")
                   .UseLocalhostClustering()
                   .AddNatsStreaming(IArgonEvent.ProviderId)
                   .AddNatsStreaming("default")
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
