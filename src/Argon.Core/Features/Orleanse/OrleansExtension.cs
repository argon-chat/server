namespace Argon.Features;

using Api.Features;
using Argon.Api.Features.Utils;
using Drains;
using Env;
using HealthChecks;
using NatsStreaming;
using Orleans.Configuration;
using Orleans.Dashboard;
using Orleans.Hosting;
using Orleans.Serialization;
using Services.Ion;
using Argon.Grains.Interfaces;
using StackExchange.Redis;

#pragma warning disable ORLEANSEXP002
#pragma warning disable ORLEANSEXP001
#pragma warning disable ORLEANSEXP003
public static class OrleansExtension
{
    extension(WebApplicationBuilder builder)
    {
        public WebApplicationBuilder AddSingleOrleansClient()
        {
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
            builder.Services.AddOrleansClient(q
                => OrleansClientFactory.Builder(q, builder.Environment, builder.Configuration));
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
                    q.ServiceId = "argon-service";
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
                    })
                   .Configure<ExceptionSerializationOptions>(x =>
                    {
                        x.SupportedNamespacePrefixes.Add("Argon");
                        x.SupportedExceptionTypeFilter = type =>
                            type == typeof(UnauthorizedAccessException)
                            
                            ;
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


                siloBuilder
                   .AddDistributedGrainDirectory()
                   .UseRedisClustering(x
                        => x.ConfigurationOptions = ConfigurationOptions.Parse(builder.Configuration.GetConnectionString("cache")!));
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
                    q.ServiceId = "argon-service";
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
                        "Default",
                        "meets"
                    ], "Npgsql", "DefaultConnection")
                   .UseRedisReminderService(x
                        => x.ConfigurationOptions = ConfigurationOptions.Parse(builder.Configuration.GetConnectionString("cache")!))
                   // AutoDeleteSchedulerGrain replaced by ScheduledTasksService (NATS WorkQueue)
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
                   .AddDistributedGrainDirectory()
                   .UseRedisClustering(x
                        => x.ConfigurationOptions = ConfigurationOptions.Parse(builder.Configuration.GetConnectionString("cache")!));
            });

            if (builder.Environment.IsWorker() || builder.Environment.IsHybrid())
            {
                builder.Services.AddSingleton<ISiloDrainService, SiloDrainService>();
                builder.Services.AddSiloHealthChecks();
                builder.Services.AddDrainAwarePlacementFilter();
            }

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