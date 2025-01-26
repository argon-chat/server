namespace Argon.Features;

using Argon.Api.Features.Orleanse;
using Env;
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
            var section = builder.Configuration.GetSection("ClusterSettings");

            var opt = new OrleansOptions();

            section.Bind(opt);


            siloBuilder.Configure<ClusterOptions>(builder.Configuration.GetSection("Orleans"))
               .AddStreaming()
               .AddActivityPropagation()
               .AddReminders()
               .UseDashboard(o => o.Port = 22832)
               .AddIncomingGrainCallFilter<SentryGrainCallFilter>()
               .When(_ => opt.UseInMemoryStreams, (x) => x.AddMemoryStreams(IArgonEvent.ProviderId))
               .When(_ => opt.UseInMemoryStreams, (x) => x.AddMemoryStreams("default"))
               .When(_ => !opt.UseInMemoryStreams, (x) => x.AddPersistentStreams("default", NatsAdapterFactory.Create, _ => { }))
               .When(_ => !opt.UseInMemoryStreams, (x) => x.AddPersistentStreams(IArgonEvent.ProviderId, NatsAdapterFactory.Create, _ => { }))
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
                   .UseAdoNetClustering(x =>
                    {
                        x.Invariant        = "Npgsql";
                        x.ConnectionString = builder.Configuration.GetConnectionString("DefaultConnection");
                    })
                   .AddBroadcastChannel(IArgonEvent.Broadcast);
            else
                siloBuilder
                   .UseLocalhostClustering()
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