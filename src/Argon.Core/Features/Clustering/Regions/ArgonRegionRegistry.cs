namespace Argon.Features.Clustering.Regions;

using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Options;
using Orleans.Configuration;
using Orleans.Messaging;
using Orleans.Serialization;
using Orleans.Serialization.Configuration;
using System.Diagnostics.CodeAnalysis;

/// <summary>A cluster client for every region, and an honest answer about which of them are up.</summary>
public interface IArgonRegionRegistry
{
    /// <summary>The region this process is in.</summary>
    string Self { get; }

    /// <summary>Every configured region, including <see cref="Self"/>.</summary>
    IReadOnlyCollection<string> Regions { get; }

    bool IsLocal(string region);

    /// <summary>The residency zone a region belongs to, or null if it is not configured.</summary>
    string? ZoneOf(string region);

    /// <summary>The other regions in a zone — the only ones work may be re-homed to.</summary>
    IReadOnlyCollection<string> PeersInZone(string zone);

    RegionStatus StatusOf(string region);

    /// <summary>
    /// The client for a region, if it is usable right now.
    /// </summary>
    /// <remarks>
    /// False for a region that is configured but not connected, which is the point: an Orleans client
    /// that has not connected does not refuse a call, it accepts it and lets it time out. Failing
    /// here costs a caller nothing and lets it route somewhere else.
    /// </remarks>
    bool TryGetClient(string region, [NotNullWhen(true)] out IClusterClient? client);

    /// <summary><see cref="TryGetClient"/>, for a caller that has nowhere else to go.</summary>
    IClusterClient GetClient(string region);
}

/// <summary>A region is configured but not usable right now.</summary>
public sealed class RegionUnavailableException(string region, RegionStatus status)
    : Exception($"Region '{region}' is {status.ToString().ToLowerInvariant()}.")
{
    public string       Region { get; } = region;
    public RegionStatus Status { get; } = status;
}

/// <summary>
/// Holds one Orleans client per remote region, and the local one.
/// </summary>
/// <remarks>
/// <para>Starting the remote clients is deliberately not part of starting the host. Orleans'
/// <c>StartAsync</c> blocks until it has reached a gateway, so awaiting it here would mean a region
/// that is down keeps this one from booting — which is the failure mode that makes multi-region
/// worse than single-region rather than better. Each peer is supervised on its own task and the
/// registry answers <see cref="RegionStatus.Connecting"/> in the meantime.</para>
/// </remarks>
public sealed class ArgonRegionRegistry : IArgonRegionRegistry, IHostedService, IAsyncDisposable
{
    private readonly ArgonRegionOptions                       options;
    private readonly IServiceProvider                         host;
    private readonly ILogger<ArgonRegionRegistry>             logger;
    private readonly Dictionary<string, RemoteRegionClient>   peers;
    private readonly Dictionary<string, string>               zones;

    public ArgonRegionRegistry(
        IOptions<ArgonRegionOptions> options,
        IServiceProvider host,
        ILogger<ArgonRegionRegistry> logger)
    {
        this.options = options.Value;
        this.host    = host;
        this.logger  = logger;

        zones = this.options.Nodes.ToDictionary(
            n => n.Key, n => n.Value.Zone ?? "", StringComparer.OrdinalIgnoreCase);

        peers = this.options.Peers.ToDictionary(
            n => n.Key,
            n => RemoteRegionClient.Create(n.Key, n.Value, this.options, host),
            StringComparer.OrdinalIgnoreCase);
    }

    public string Self => options.Self;

    public IReadOnlyCollection<string> Regions
        => options.Nodes.Count == 0 ? [options.Self] : options.Nodes.Keys;

    public bool IsLocal(string region)
        => string.Equals(region, options.Self, StringComparison.OrdinalIgnoreCase);

    public string? ZoneOf(string region)
        => zones.TryGetValue(region, out var zone) && zone.Length > 0 ? zone : null;

    public IReadOnlyCollection<string> PeersInZone(string zone)
        => zones.Where(z => z.Value.Equals(zone, StringComparison.OrdinalIgnoreCase))
           .Select(z => z.Key)
           .Where(r => !IsLocal(r))
           .ToArray();

    public RegionStatus StatusOf(string region)
    {
        if (IsLocal(region))
            return RegionStatus.Online;
        return peers.TryGetValue(region, out var peer) ? peer.Status : RegionStatus.Offline;
    }

    public bool TryGetClient(string region, [NotNullWhen(true)] out IClusterClient? client)
    {
        if (IsLocal(region))
        {
            client = host.GetRequiredService<IClusterClient>();
            return true;
        }

        client = null;

        if (!peers.TryGetValue(region, out var peer) || peer.Status != RegionStatus.Online)
            return false;

        client = peer.Client;
        return true;
    }

    public IClusterClient GetClient(string region)
        => TryGetClient(region, out var client)
            ? client
            : throw new RegionUnavailableException(region, StatusOf(region));

    public Task StartAsync(CancellationToken cancellationToken)
    {
        if (peers.Count == 0)
        {
            logger.LogInformation("Single region '{Region}'; no peers configured", options.Self);
            return Task.CompletedTask;
        }

        logger.LogInformation("Region '{Region}' connecting to {Count} peer(s): {Peers}",
            options.Self, peers.Count, string.Join(", ", peers.Keys));

        // Not awaited, and that is the whole design of this class.
        foreach (var peer in peers.Values)
            peer.Start();

        return Task.CompletedTask;
    }

    public async Task StopAsync(CancellationToken cancellationToken)
        => await DisposeAsync();

    public async ValueTask DisposeAsync()
    {
        foreach (var peer in peers.Values)
            await peer.DisposeAsync();
        peers.Clear();
    }
}

/// <summary>
/// One remote region's Orleans client, its own container, and the task that keeps it connected.
/// </summary>
/// <remarks>
/// <para>An Orleans client needs a service provider of its own — <c>AddOrleansClient</c> builds one
/// and a child provider cannot see a parent — and the temptation is to bridge the two by proxying
/// host services into it one at a time, which is how this was done before and how it stays broken:
/// the list of proxied services is whatever happened to be needed the last time something threw.</para>
///
/// <para>It is not necessary. <c>AddOrleansClient</c> calls <c>AddLogging()</c> and
/// <c>AddSerializer()</c> itself, so the container is self-sufficient; the only thing borrowed is the
/// host's <c>ILoggerFactory</c>, so a remote region logs where everything else does. Argon's own
/// types go in as <em>instances</em> built out here, closing over what they need, so nothing inside
/// ever reaches back out.</para>
///
/// <para>The one thing that does have to be copied is the type manifest: the client must know the
/// same grain interfaces the silos do, and it learns them from
/// <c>IConfigureOptions&lt;TypeManifestOptions&gt;</c>. Applying those twice is harmless — every
/// collection behind them is a set or a dictionary — so copying is safe even where the client's own
/// assembly scan would have found them anyway.</para>
/// </remarks>
public sealed class RemoteRegionClient : IAsyncDisposable
{
    private readonly string                      region;
    private readonly ServiceProvider             provider;
    private readonly IClusterClient              client;
    private readonly RegionConnectionRetryFilter retryFilter;
    private readonly ILogger                     logger;
    private readonly CancellationTokenSource     lifetime = new();
    private readonly TimeSpan                    maxBackoff;

    private Task?    supervisor;
    private int      status = (int)RegionStatus.Connecting;
    private int      disposed;

    private RemoteRegionClient(
        string region,
        ServiceProvider provider,
        IClusterClient client,
        RegionConnectionRetryFilter retryFilter,
        TimeSpan maxBackoff,
        ILogger logger)
    {
        this.region      = region;
        this.provider    = provider;
        this.client      = client;
        this.retryFilter = retryFilter;
        this.maxBackoff  = maxBackoff;
        this.logger      = logger;
    }

    public IClusterClient Client => client;

    public RegionStatus Status => (RegionStatus)Volatile.Read(ref status);

    public static RemoteRegionClient Create(
        string region, ArgonRegionNode node, ArgonRegionOptions options, IServiceProvider host)
    {
        var loggerFactory = host.GetRequiredService<ILoggerFactory>();
        var logger        = loggerFactory.CreateLogger($"Argon.Regions.{region}");

        if (!ArgonRegionNode.TryParseGateway(node.Gateway, out var gatewayHost, out var gatewayPort))
            throw new InvalidOperationException(
                $"Region '{region}' has gateway '{node.Gateway}', which is not 'host:port'. " +
                "Configuration validation should have caught this.");

        // No default: a cluster id is a value from the other region's configuration, and guessing it
        // produces a client that waits forever for a gateway it is not allowed to talk to.
        if (string.IsNullOrWhiteSpace(node.ClusterId))
            throw new InvalidOperationException(
                $"Region '{region}' has no ClusterId. Configuration validation should have caught this.");

        var clusterId = node.ClusterId;

        var retryFilter = new RegionConnectionRetryFilter(region, options.MaxReconnectBackoff, logger);

        // Filled in once the instance exists; the observer is constructed first because the client
        // container needs it, and it only ever runs after everything is built.
        RemoteRegionClient? built = null;
        var observer = new RegionConnectionObserver(region, s => built?.Report(s), logger);

        var services = new ServiceCollection();

        // The only thing taken from the host, so a remote region's logs land with everything else.
        // Registered before AddOrleansClient because AddLogging() uses TryAdd and would otherwise win.
        services.AddSingleton(loggerFactory);

        // Instances, not registrations: neither of these resolves anything from the host, so the two
        // containers stay strangers.
        services.AddSingleton<IClientConnectionRetryFilter>(retryFilter);
        services.AddSingleton<IClusterConnectionStatusObserver>(observer);

        // The same catch-all the silos register, and it has to be the same one. Most types crossing a
        // grain boundary carry no [GenerateSerializer], so Orleans has no generated codec for them and
        // falls through to this — for the wire and for the deep copy both. Without it the client
        // cannot even construct a grain reference: building the proxy asks for a copier per argument
        // type, and the first one it cannot find throws.
        services.AddArgonSerializer();

        services.AddOrleansClient(builder =>
        {
            builder.Configure<ClusterOptions>(o =>
            {
                o.ClusterId = clusterId;
                o.ServiceId = node.ResolvedServiceId();
            });

            // Its own timeout, much shorter than the local one. A region that is slow rather than
            // down is the case this exists for.
            builder.Configure<ClientMessagingOptions>(o => o.ResponseTimeout = options.RemoteResponseTimeout);

            builder.Configure<GatewayOptions>(o => o.GatewayListRefreshPeriod = options.GatewayRefreshPeriod);

            builder.Configure<ExceptionSerializationOptions>(o => o.SupportedNamespacePrefixes.Add("Argon"));

            builder.Services.AddSingleton<IGatewayListProvider>(_ => new RegionGatewayListProvider(
                region, gatewayHost, gatewayPort, options.GatewayRefreshPeriod, logger));
        });

        // The grain interfaces. Applying the host's manifest providers here is idempotent, and it is
        // what makes GetGrain<IChannelGrain>() on this client resolve to the same interface id the
        // far silo publishes.
        foreach (var manifest in host.GetServices<IConfigureOptions<TypeManifestOptions>>())
            services.AddSingleton(manifest);

        var provider = services.BuildServiceProvider();
        var client   = provider.GetRequiredService<IClusterClient>();

        built = new RemoteRegionClient(region, provider, client, retryFilter, options.MaxReconnectBackoff, logger);
        return built;
    }

    public void Start()
        => supervisor ??= Task.Run(() => SuperviseAsync(lifetime.Token), CancellationToken.None);

    private void Report(RegionStatus next)
    {
        var previous = (RegionStatus)Interlocked.Exchange(ref status, (int)next);

        if (next == RegionStatus.Online && previous != RegionStatus.Online)
            retryFilter.Connected();
    }

    /// <summary>
    /// Keeps trying, forever, and never lets a failure out.
    /// </summary>
    /// <remarks>
    /// The retry filter already keeps <c>StartAsync</c> from returning while it can still retry, so
    /// this loop is what catches anything thrown outside that path — and, more importantly, it is
    /// the reason there is no <c>await</c> on a remote region anywhere near host startup.
    /// </remarks>
    private async Task SuperviseAsync(CancellationToken ct)
    {
        var attempt = 0;

        while (!ct.IsCancellationRequested)
        {
            try
            {
                await ((IHostedService)client).StartAsync(ct);

                // Only out of Connecting. The observer may already have said Offline while this was
                // in flight, and it is the one that is right.
                Interlocked.CompareExchange(ref status, (int)RegionStatus.Online, (int)RegionStatus.Connecting);
                retryFilter.Connected();

                logger.LogInformation("Region '{Region}' connected", region);
                return;
            }
            catch (OperationCanceledException) when (ct.IsCancellationRequested)
            {
                return;
            }
            catch (Exception e)
            {
                Volatile.Write(ref status, (int)RegionStatus.Offline);

                var delay = RegionConnectionRetryFilter.Backoff(++attempt, maxBackoff);
                logger.LogWarning(e, "Region '{Region}' failed to connect; retrying in {Delay}", region, delay);

                try
                {
                    await Task.Delay(delay, ct);
                }
                catch (OperationCanceledException)
                {
                    return;
                }
            }
        }
    }

    public async ValueTask DisposeAsync()
    {
        if (Interlocked.Exchange(ref disposed, 1) == 1)
            return;

        await lifetime.CancelAsync();

        if (supervisor is not null)
        {
            try
            {
                await supervisor;
            }
            catch (Exception e)
            {
                logger.LogDebug(e, "Region '{Region}' supervisor ended with an error", region);
            }
        }

        try
        {
            using var stopping = new CancellationTokenSource(TimeSpan.FromSeconds(5));
            await ((IHostedService)client).StopAsync(stopping.Token);
        }
        catch (Exception e)
        {
            // Shutting down a client that never connected throws, and there is nothing to do about
            // it: the process is going away and so is the connection.
            logger.LogDebug(e, "Region '{Region}' client did not stop cleanly", region);
        }

        await provider.DisposeAsync();
        lifetime.Dispose();
    }
}
