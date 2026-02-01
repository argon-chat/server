#pragma warning disable ORLEANSEXP002
#pragma warning disable ORLEANSEXP001

namespace Argon.Features;

using Api.Features.Orleans.Consul;
using Argon.Api.Features.Utils;
using EntryPoint;
using Env;
using NatsStreaming;
using Orleans.Configuration;
using Orleans.Dashboard;
using Orleans.Hosting;
using Orleans.Serialization;
using Services.Ion;

public static class OrleansExtension
{
    extension(WebApplicationBuilder builder)
    {
        public WebApplicationBuilder AddMultiOrleansClient()
        {
            builder.AddNatsCtx();
            builder.Services.AddSingleton<IArgonDcRegistry, ArgonDcRegistry>();
            builder.Services.AddHostedService<DcWatcherService>();
            builder.Services.AddSingleton<IClusterClientFactory, OrleansClientFactory>();
            builder.Services.AddHostedService<EntryPointWatcher>();
            return builder;
        }

        public WebApplicationBuilder AddShimsForHybridRole()
        {
            builder.AddNatsCtx();
            builder.Services.AddSingleton<IArgonDcRegistry, ArgonHybridDcRegistry>();
            return builder;
        }


        public WebApplicationBuilder AddSingleOrleansClient()
        {
            //builder.AddMultiOrleansClient();
            //return builder;
            builder.Services.AddSerializer(x =>
            {
                x.AddNewtonsoftJsonSerializer(q => true,
                    optionsBuilder =>
                    {
                        optionsBuilder.Configure(z =>
                        {
                            z.SerializerSettings                       ??= new JsonSerializerSettings();
                            z.SerializerSettings.ReferenceLoopHandling =   ReferenceLoopHandling.Ignore;
                            z.SerializerSettings.Converters.Add(new MessageEntityConverter());
                            z.SerializerSettings.Converters.Add(new UlongEnumConverter<ArgonEntitlement>());
                            z.SerializerSettings.Converters.Add(new IonMaybeConverter());
                            z.SerializerSettings.Converters.Add(new IonArrayConverter());
                            z.SerializerSettings.Converters.Add(new StringEnumConverter());
                        });
                    });
            });
            builder.AddNatsCtx();
            builder.Services.AddSingleton<IArgonDcRegistry, ArgonDcRegistry>();
            builder.Services.AddHostedService<EntryPointWatcher>();
            builder.Services.AddOrleansClient(q
                => OrleansClientFactory.Builder(q, builder.Environment, builder.Configuration, builder.GetDatacenter()));
            return builder;
        }

        public WebApplicationBuilder AddGatewayOrleans()
        {
            builder.Services.AddSerializer(x =>
            {
                x.AddNewtonsoftJsonSerializer(q => true, optionsBuilder =>
                {
                    optionsBuilder.Configure(z =>
                    {
                        z.SerializerSettings                       ??= new JsonSerializerSettings();
                        z.SerializerSettings.ReferenceLoopHandling =   ReferenceLoopHandling.Ignore;
                        z.SerializerSettings.Converters.Add(new MessageEntityConverter());
                        z.SerializerSettings.Converters.Add(new UlongEnumConverter<ArgonEntitlement>());
                        z.SerializerSettings.Converters.Add(new IonMaybeConverter());
                        z.SerializerSettings.Converters.Add(new IonArrayConverter());
                        z.SerializerSettings.Converters.Add(new StringEnumConverter());
                    });
                });
            });

            builder.Host.UseOrleans(siloBuilder =>
            {
                siloBuilder.ConfigureEndpoints(11111, 30000).AddDashboard();

                siloBuilder.Configure<ClusterOptions>(q =>
                {
                    q.ClusterId = "argon-cluster";
                    q.ServiceId = $"argon-region-{builder.GetDatacenter()}";
                });
                siloBuilder
                   .UseStorages([
                        IUserSessionGrain.StorageId,
                        IServerInvitesGrain.StorageId,
                        "Default"
                    ], "Npgsql", "DefaultConnection")
                   .Configure<ClusterMembershipOptions>(options =>
                    {
                        options.IAmAliveTablePublishTimeout = TimeSpan.FromSeconds(10);
                        options.TableRefreshTimeout         = TimeSpan.FromSeconds(10);
                        options.MaxJoinAttemptTime          = TimeSpan.FromSeconds(10);
                        options.DefunctSiloExpiration       = TimeSpan.FromSeconds(60);
                        //options.LivenessEnabled             = false; // TODO
                    })
                   .Configure<ExceptionSerializationOptions>(x => { x.SupportedNamespacePrefixes.Add("Argon"); })
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

                siloBuilder
                   .AddConsulGrainDirectory("servers")
                   .AddConsulGrainDirectory("channels")
                   .AddConsulGrainDirectory("@meet/meetings")
                   .AddConsulGrainDirectory("@meet/invite-codes")
                   .AddConsulGrainDirectory("@meet/join-requests")
                   .AddConsulGrainDirectory("@meet/meeting-quotas")
                   .AddConsulClustering();
            });

            return builder;
        }

        public WebApplicationBuilder AddWorkerOrleans()
        {
            builder.AddNatsCtx();

            // Register Orleans rebalancing providers explicitly
            builder.Services.AddSingleton<ArgonRebalancerBackoffProvider>();
            builder.Services.AddSingleton<ArgonImbalanceToleranceRule>();

            builder.Services.AddSerializer(x =>
            {
                x.AddNewtonsoftJsonSerializer(q => true, optionsBuilder =>
                {
                    optionsBuilder.Configure(z =>
                    {
                        z.SerializerSettings                       ??= new JsonSerializerSettings();
                        z.SerializerSettings.ReferenceLoopHandling =   ReferenceLoopHandling.Ignore;
                        z.SerializerSettings.Converters.Add(new MessageEntityConverter());
                        z.SerializerSettings.Converters.Add(new UlongEnumConverter<ArgonEntitlement>());
                        z.SerializerSettings.Converters.Add(new IonMaybeConverter());
                        z.SerializerSettings.Converters.Add(new IonArrayConverter());
                        z.SerializerSettings.Converters.Add(new StringEnumConverter());
                    });
                });
            });
            builder.Host.UseOrleans(siloBuilder =>
            {
                if (builder.IsGatewayRole() || builder.IsHybridRole())
                    siloBuilder.ConfigureEndpoints(11111, 30000).AddDashboard();
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
                    //.AddActivationRebalancer<ArgonRebalancerBackoffProvider>()
                   .AddActivationRepartitioner<ArgonImbalanceToleranceRule>()
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
                   .Configure<ExceptionSerializationOptions>(x => { x.SupportedNamespacePrefixes.Add("Argon"); })
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

                siloBuilder
                   .AddConsulGrainDirectory("servers")
                   .AddConsulGrainDirectory("channels")
                   .AddConsulGrainDirectory("@meet/meetings")
                   .AddConsulGrainDirectory("@meet/invite-codes")
                   .AddConsulGrainDirectory("@meet/join-requests")
                   .AddConsulGrainDirectory("@meet/meeting-quotas")
                   .AddConsulClustering();
            });
            return builder;
        }


        
    }

    public static ISiloBuilder UseStorages(this ISiloBuilder builder, List<string> keys, string invariant, string connString)
    {
        foreach (var key in keys)
            builder.AddRedisStorage(key, 0);

        return builder;
    }
}