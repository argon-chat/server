namespace Argon.Features;

using System;
using Api.Features;
using Api.Features.Orleans.Consul;
using EntryPoint;
using Env;
using Orleans.Providers;
using NatsStreaming;
using Orleans.Configuration;
using Sentry;
using Services;
using Orleans.Hosting;
using Orleans.Serialization;

#pragma warning disable ORLEANSEXP001

public static class OrleansExtension
{
    public static WebApplicationBuilder AddMultiOrleansClient(this WebApplicationBuilder builder)
    {
        builder.AddNatsCtx();
        builder.Services.AddSingleton<IArgonDcRegistry, ArgonDcRegistry>();
        builder.Services.AddHostedService<DcWatcherService>();
        builder.Services.AddSingleton<IClusterClientFactory, OrleansClientFactory>();
        builder.Services.AddHostedService<EntryPointWatcher>();
        return builder;
    }

    public static WebApplicationBuilder AddShimsForHybridRole(this WebApplicationBuilder builder)
    {
        builder.AddNatsCtx();
        builder.Services.AddSingleton<IArgonDcRegistry, ArgonHybridDcRegistry>();
        return builder;
    }

    public static WebApplicationBuilder AddSingleOrleansClient(this WebApplicationBuilder builder)
    {
        //builder.AddMultiOrleansClient();
        //return builder;
        builder.AddNatsCtx();
        builder.Services.AddSingleton<IArgonDcRegistry, ArgonDcRegistry>();
        builder.Services.AddHostedService<EntryPointWatcher>();
        builder.Services.AddOrleansClient(q => OrleansClientFactory.Builder(q, builder.Environment, builder.Configuration, builder.GetDatacenter()));
        return builder;
    }

    public static WebApplicationBuilder AddWorkerOrleans(this WebApplicationBuilder builder)
    {
        builder.AddNatsCtx();
        builder.Services.UseOrleansMessagePack();
        builder.Host.UseOrleans(siloBuilder =>
        {
            if (builder.IsGatewayRole() || builder.IsHybridRole())
                siloBuilder.ConfigureEndpoints(11111, 30000).UseDashboard(o => o.Port = 22832);
            else if (builder.IsWorkerRole())
                siloBuilder.ConfigureEndpoints(11111, 0);
            else
                throw new InvalidOperationException("Cannot determine configuration for worker silo");

            siloBuilder.Configure<ClusterOptions>(q =>
            {
                q.ClusterId = "argon-cluster";
                q.ServiceId = $"argon-region-{builder.GetDatacenter()}";
            });
            siloBuilder
               .AddStreaming()
               .AddActivityPropagation()
               .AddReminders()
               .AddIncomingGrainCallFilter<SentryGrainCallFilter>()
               .AddIncomingGrainCallFilter<MetricGrainCallFilter>()
               .UseStorages([
                    IUserSessionGrain.StorageId,
                    IServerInvitesGrain.StorageId,
                    "Default"
                ], "Npgsql", "DefaultConnection")
               .UseInMemoryReminderService()
               .Configure<ClusterMembershipOptions>(options =>
                {
                    options.IAmAliveTablePublishTimeout = TimeSpan.FromSeconds(10);
                    options.TableRefreshTimeout         = TimeSpan.FromSeconds(10);
                    options.MaxJoinAttemptTime          = TimeSpan.FromSeconds(10);
                    options.DefunctSiloExpiration       = TimeSpan.FromSeconds(60);
                    //options.LivenessEnabled             = false; // TODO
                })
               .Configure<ExceptionSerializationOptions>(x =>
                {
                    x.SupportedNamespacePrefixes.Add("Argon");
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

            if (builder.Environment.IsMultiRegion() || builder.Environment.IsSingleRegion())
                siloBuilder
                   .AddConsulGrainDirectory("servers")
                   .AddConsulGrainDirectory("channels")
                   .AddConsulClustering();
            else
                siloBuilder
                   .AddInMemoryGrainDirectory("servers")
                   .AddInMemoryGrainDirectory("channels")
                   .UseLocalhostClustering();
        });

        return builder;
    }


    public static ISiloBuilder UseStorages(this ISiloBuilder builder, List<string> keys, string invariant, string connString)
    {
        foreach (var key in keys)
            builder.AddRedisStorage(key, 0);

        return builder;
    }
}