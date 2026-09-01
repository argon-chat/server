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
    {
        // Having a codec for a type is not the same as being allowed to name it, and the catch-all
        // below only supplies the first. See IonUnionTypeFilter for what the second one cost.
        services.AddSingleton<ITypeFilter, IonUnionTypeFilter>();

        return services.AddSerializer(x => x.AddNewtonsoftJsonSerializer(_ => true, options =>
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
    }

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

                // NO ClusterMembershipOptions BLOCK. There was one, tightening four timeouts — table
                // publish and refresh to 10s, join to 2min, defunct expiry to 60s. It is gone as of
                // Orleans 10.3, and what it was reaching for is now done better by something else.
                //
                // None of those four is a probe setting, which is the thing that actually detects a
                // dead silo. Probes are on the defaults and always were: 5s timeout, three missed
                // before suspicion, up to ten silos watched each. 10.3 made that timeout and the
                // interval between probes ADAPTIVE — tuned per monitored silo from its observed
                // response times, bounded by MinProbeTimeout and MaxProbeTimeout — and added
                // connection liveness checks and local-stall detection on top. Leaving those alone
                // is what lets any of it work.
                //
                // So the four were buying convergence through the membership TABLE, at six times the
                // read traffic and three times the write traffic against the clustering store, for a
                // path that is not how failure is noticed. This cluster has six silo roles and
                // NumProbedSilos defaults to ten, so every silo probes every other one directly:
                // there is nobody whose death only the table could reveal.
                //
                // DefunctSiloExpiration is the one whose default is not a wash — 60s becomes seven
                // days, so dead rows now linger rather than being swept within the minute. That is
                // upstream's own default and the entries are inert, but a stand that redeploys
                // eleven roles on every push accumulates them, and it is the number to reach for
                // first if the clustering store starts looking untidy.
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

            // EVERY SILO, NOT THE ONES THAT HOST IRemindable GRAINS.
            //
            // This was gated on `role.UsesReminders`, on the reasoning that a silo hosting no
            // IRemindable grain has no reason to poll the reminder table. That reasoning is wrong,
            // and the way it is wrong takes a cluster down rather than merely wasting a poll.
            //
            // Reminder operations are not served by the silo that issues them. They are routed
            // across the cluster, and a silo that never called AddReminders has no such system
            // target to receive them — so the call is rejected with "SystemTarget
            // sys.svc.user.<hash>/<address> not active on this silo". Observed on the dev stand with
            // reminders on `core` and `jobs` only: jobs registering its own reminder was routed to
            // `moderation`, and a user session on `core` asking for one was routed to `commerce`.
            // Neither had the service.
            //
            // The consequences were not symmetrical, and the quieter one is worse. `jobs` failed in
            // a startup task and crash-looped, which is at least visible. On `core` the rejection
            // surfaced through the session grain, so the SignalR connection closed, the client
            // reconnected, activated the session again, and failed again -- a reconnect loop that
            // reads on the client as the server refusing it, with nothing in that log naming
            // reminders at all.
            //
            // The poll this was avoiding is also smaller than it looked: the reminder table is
            // partitioned across the silos that serve it, so adding silos divides the same work
            // rather than repeating it.
            //
            // `UsesReminders` and validation rule E3 still describe which roles HOST an IRemindable
            // grain, which is a real question -- but it is not this one, and nothing here may branch
            // on it again.
            silo.AddReminders()
               .UseRedisReminderService(x => x.ConfigurationOptions =
                    new RedisProfileRegistry(builder.Configuration).BuildOptions(RedisProfiles.Orleans));

            // The declaration in the role drives validation (E5); the action itself still has to name
            // the grain and the method, so it stays explicit and gated on the declaration.
            if (role.StartupCalls.Contains(typeof(IAutoDeleteSchedulerGrain)))
                silo.AddStartupTask(async (sp, _) => await sp.GetRequiredService<IGrainFactory>()
                   .GetGrain<IAutoDeleteSchedulerGrain>(IAutoDeleteSchedulerGrain.SingletonId)
                   .EnsureSchedulerActiveAsync());

            if (role.StartupCalls.Contains(typeof(ITtlSweepGrain)))
                silo.AddStartupTask(async (sp, _) => await sp.GetRequiredService<IGrainFactory>()
                   .GetGrain<ITtlSweepGrain>(ITtlSweepGrain.SingletonId)
                   .EnsureSweeperActiveAsync());

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
