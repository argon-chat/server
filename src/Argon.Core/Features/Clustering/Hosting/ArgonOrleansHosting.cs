namespace Argon.Features.Clustering;

using Argon.Api.Features.Utils;
using Argon.Features.k8s;
using Argon.Grains.Interfaces;
using Argon.Services;
using Drains;
using HealthChecks;
using NatsStreaming;
using Orleans.Configuration;
using Orleans.Storage;
using Features.Orleanse.Storages;
using Orleans.Dashboard;
using Orleans.Serialization;
using Services.Ion;

#pragma warning disable ORLEANSEXP002
#pragma warning disable ORLEANSEXP001
#pragma warning disable ORLEANSEXP003

/// <summary>
/// Orleans hosting driven by the resolved role: a silo restricted to the grains the role hosts, or
/// a client that hosts none.
/// </summary>
/// <remarks>
/// Replaces <c>AddWorkerOrleans</c> / <c>AddGatewayOrleans</c> / <c>AddSingleOrleansClient</c> /
/// <c>AddShimsForHybridRole</c>, which branched on <c>ArgonRoleKind</c> and carried three
/// byte-identical copies of the serializer configuration between them.
/// </remarks>
public static class ArgonOrleansHosting
{
    /// <summary>
    /// Storage providers are core configuration, identical on every silo rather than declared per
    /// role: a role never has to register a provider on another role's behalf.
    /// </summary>
    private static readonly List<string> StorageProviders =
    [
        IUserSessionGrain.StorageId,
        IServerInvitesGrain.StorageId,
        "Default",
        "meets"
    ];

    public static IReadOnlySet<string> KnownStorageProviders { get; } =
        StorageProviders.Append(VolatileGrainStorage.ProviderName).ToHashSet(StringComparer.Ordinal);

    public static WebApplicationBuilder AddArgonOrleans(this WebApplicationBuilder builder, RoleDescriptor role)
    {
        // Every role, not just the ones that hold cluster clients for other regions: a silo mints
        // channel ids and an entry point mints space and user ids, and both have to stamp the region
        // they are in. Before anything can construct an object, because an id can be minted during
        // startup.
        builder.Services.Configure<Regions.ArgonRegionOptions>(
            builder.Configuration.GetSection(Regions.ArgonRegionOptions.SectionName));

        Regions.ArgonId.UseRegion(
            Regions.ArgonRegionOptions.SelfIndexOf(builder.Configuration),
            Regions.ArgonRegionOptions.EpochOf(builder.Configuration));

        return role.IsClient ? builder.AddArgonOrleansClient() : builder.AddArgonSilo(role);
    }

    // ── shared ───────────────────────────────────────────────────────────────────────────────

    /// <summary>
    /// The one serializer configuration. It used to exist three times, and a converter added to one
    /// copy was a converter missing from the other two.
    /// </summary>
    private static WebApplicationBuilder AddArgonSerializer(this WebApplicationBuilder builder)
    {
        builder.Services.AddArgonSerializer();
        return builder;
    }

    /// <summary>
    /// The catch-all serializer every Argon process has to agree on.
    /// </summary>
    /// <remarks>
    /// <para>Most types crossing a grain boundary have no <c>[GenerateSerializer]</c>, so Orleans has
    /// no generated codec for them and falls through to this one — for the wire <em>and</em> for the
    /// in-silo deep copy, which is why its absence shows up as "copier not found" while building a
    /// grain reference rather than as a failed call.</para>
    ///
    /// <para>On a service collection rather than on the host builder because a cluster client for
    /// another region builds a container of its own and needs exactly this registration. Two
    /// copies of it that drift would not fail — they would disagree about the wire, in one direction,
    /// between regions.</para>
    /// </remarks>
    public static IServiceCollection AddArgonSerializer(this IServiceCollection services)
        => services.AddSerializer(x => x.AddNewtonsoftJsonSerializer(_ => true, options =>
            options.Configure(z =>
            {
                z.SerializerSettings                       ??= new JsonSerializerSettings();
                z.SerializerSettings.ReferenceLoopHandling =   ReferenceLoopHandling.Ignore;
                z.SerializerSettings.Converters.Add(new MessageEntityConverter());
                z.SerializerSettings.Converters.Add(new UlongEnumConverter<ArgonEntitlement>());
                z.SerializerSettings.Converters.Add(new IonMaybeConverter());
                z.SerializerSettings.Converters.Add(new IonArrayConverter());
                z.SerializerSettings.Converters.Add(new StringEnumConverter());
            })));

    private static string DatacenterOf(WebApplicationBuilder builder)
        => ArgonDatacenter.Current;

    // ── client ───────────────────────────────────────────────────────────────────────────────

    private static WebApplicationBuilder AddArgonOrleansClient(this WebApplicationBuilder builder)
    {
        builder.AddArgonDatacenter();
        builder.AddArgonSerializer();
        builder.AddNatsCtx();
        builder.Services.AddSingleton<IArgonDcRegistry, ArgonDcRegistry>();

        // Registered before the client is built, into this same container: an in-host client has no
        // container of its own, so Orleans resolves the observer from here. This is the whole of what
        // a client role knows about the cluster — there is no membership table on this side — and
        // both probes and the pre-stop wait are answered from it.
        builder.Services.AddSingleton<ClusterClientStatus>();
        builder.Services.AddSingleton<IClusterConnectionStatusObserver>(
            sp => sp.GetRequiredService<ClusterClientStatus>());

        builder.Services.AddOrleansClient(q =>
            OrleansClientFactory.Builder(q, builder.Environment, builder.Configuration, DatacenterOf(builder)));

        // After the client's own hosted service, deliberately: that one connects during StartAsync
        // and blocks until it succeeds, so this one running is proof a gateway answered. It is the
        // floor under the observer, for a runtime that raises no notification at all.
        builder.Services.AddHostedService(sp => sp.GetRequiredService<ClusterClientStatus>());

        builder.Services.AddSingleton<ClientStopSignal>();
        builder.Services.AddClientHealthChecks();

        return builder;
    }

    // ── silo ─────────────────────────────────────────────────────────────────────────────────

    private static WebApplicationBuilder AddArgonSilo(this WebApplicationBuilder builder, RoleDescriptor role)
    {
        var datacenter = DatacenterOf(builder);
        var endpoints  = ArgonClusterEndpoints.Resolve(builder.Configuration);

        builder.AddArgonDatacenter();
        builder.AddArgonSerializer();
        builder.AddNatsCtx();
        builder.Services.AddSingleton<ArgonRebalancerBackoffProvider>();
        builder.Services.AddSingleton<ArgonImbalanceToleranceRule>();
        builder.Services.AddSingleton<IArgonDcRegistry, ArgonDcRegistry>();

        builder.Host.UseOrleans(silo =>
        {
            // A role that exposes the client gateway also serves the dashboard; the rest keep the
            // gateway port closed so nothing can connect to them directly.
            if (role.ExposesClusterGateway)
                silo.ConfigureEndpoints(endpoints.SiloPort, endpoints.GatewayPort).AddDashboard();
            else
                silo.ConfigureEndpoints(endpoints.SiloPort, 0);

            silo.UseArgonGrainTypes(role);

            silo.Configure<ClusterOptions>(q =>
            {
                q.ClusterId = endpoints.ClusterId;
                q.ServiceId = endpoints.ServiceId;
            });

            silo.AddStreaming()
               .AddActivityPropagation()
               .AddActivationRepartitioner<ArgonImbalanceToleranceRule>()
               .UseRedisStorages(StorageProviders)

                // Stores nothing. It is here so a grain can declare in-memory state as
                // IPersistentState and have the runtime carry it across a migration for free.
               .AddVolatileStorage()
               .Configure<ClusterMembershipOptions>(options =>
                {
                    options.IAmAliveTablePublishTimeout = TimeSpan.FromSeconds(10);
                    options.TableRefreshTimeout         = TimeSpan.FromSeconds(10);
                    options.MaxJoinAttemptTime          = TimeSpan.FromMinutes(2);
                    options.DefunctSiloExpiration       = TimeSpan.FromSeconds(60);
                })
               .Configure<ExceptionSerializationOptions>(x => x.SupportedNamespacePrefixes.Add("Argon"))
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

            // Reminders are per-role: a silo that hosts no IRemindable grain has no reason to poll
            // the reminder table. Validation rule E3 keeps the flag honest.
            if (role.UsesReminders)
                silo.AddReminders()
                   .UseRedisReminderService(x => x.ConfigurationOptions =
                        new RedisProfileRegistry(builder.Configuration).BuildOptions(RedisProfiles.Orleans));

            // The declaration in the role drives validation (E5); the action itself still has to name
            // the grain and the method, so it stays explicit and gated on the declaration.
            if (role.StartupCalls.Contains(typeof(IAutoDeleteSchedulerGrain)))
                silo.AddStartupTask(async (sp, _) => await sp.GetRequiredService<IGrainFactory>()
                   .GetGrain<IAutoDeleteSchedulerGrain>(IAutoDeleteSchedulerGrain.SingletonId)
                   .EnsureSchedulerActiveAsync());

            silo.AddDistributedGrainDirectory()
               .UseRedisClustering(x => x.ConfigurationOptions =
                    new RedisProfileRegistry(builder.Configuration).BuildOptions(RedisProfiles.Orleans));
        });

        builder.Services.AddSingleton<ISiloDrainService, SiloDrainService>();
        builder.Services.AddSiloHealthChecks();
        builder.Services.AddDrainAwarePlacementFilter();

        return builder;
    }
}
