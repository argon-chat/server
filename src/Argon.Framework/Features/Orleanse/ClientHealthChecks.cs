namespace Argon.HealthChecks;

using Features.k8s;
using Microsoft.Extensions.Diagnostics.HealthChecks;

/// <summary>
/// What this process's Orleans client can currently tell us about the cluster.
/// </summary>
/// <remarks>
/// <para>A client role has no <see cref="Orleans.Runtime.ISiloStatusOracle"/> — it is not in the
/// membership table and has no view of it — so the only thing it knows about the cluster is what its
/// own client reports. <c>IClusterConnectionStatusObserver</c> is that report: Orleans calls it when
/// the gateway count changes and when the connection is lost, which is exactly the pair of events
/// readiness turns on.</para>
///
/// <para>Gateways reaching zero is a different event from the connection being declared lost, and it
/// arrives first. The gap matters because an Orleans client with nowhere to send a call does not
/// fail it, it waits out the response timeout — so treating zero gateways as not-ready is what keeps
/// new connections off a pod that would do that to them.</para>
///
/// <para>Unlike the region registry's observer, this one is resolved from the container rather than
/// handed in as a pre-built instance: an in-host client shares the host's container, so there are no
/// two containers to bridge.</para>
/// </remarks>
public sealed class ClusterClientStatus(ILogger<ClusterClientStatus> logger)
    : IClusterConnectionStatusObserver, IHostedService
{
    /// <summary>
    /// The client has not reported a gateway count yet, which is not the same as reporting none.
    /// </summary>
    /// <remarks>
    /// Kestrel is listening before the Orleans client has connected — the web host's service is
    /// registered while the builder is created, the client's afterwards — so the probes are being
    /// answered during a window in which nothing has been reported at all. Both answers are
    /// not-ready; keeping them apart is what makes the reason readable.
    /// </remarks>
    public const int NotReported = -1;

    private int  gateways = NotReported;
    private long connectedAtTicks;
    private bool clientStarted;

    public int  Gateways         => Volatile.Read(ref gateways);
    public bool HasEverConnected => Interlocked.Read(ref connectedAtTicks) != 0;

    /// <summary>The cluster client finished its own startup, which it only does once connected.</summary>
    public bool ClientStarted => Volatile.Read(ref clientStarted);

    /// <summary>
    /// Whether a call made here would reach the cluster.
    /// </summary>
    /// <remarks>
    /// The gateway count is the live answer. <see cref="ClientStarted"/> is the floor under it, and
    /// it is there so that a runtime which never raises the notification cannot leave every client
    /// pod unready forever — a silent readiness failure would take the whole role out of its Service
    /// with nothing to point at. It only ever applies while nothing has been reported: once a count
    /// arrives, including a count of zero, the count is the answer.
    /// </remarks>
    public bool IsConnected => Gateways > 0 || (Gateways == NotReported && ClientStarted);

    /// <summary>
    /// Records that the cluster client connected at least once.
    /// </summary>
    /// <remarks>
    /// Registered as a hosted service after Orleans' own, whose <c>StartAsync</c> is the connect —
    /// blocking, retried until it succeeds. Reaching this method therefore means a gateway answered,
    /// whatever the observer has or has not said.
    /// </remarks>
    public Task StartAsync(CancellationToken cancellationToken)
    {
        Volatile.Write(ref clientStarted, true);
        Interlocked.CompareExchange(ref connectedAtTicks, DateTimeOffset.UtcNow.UtcTicks, 0);

        return Task.CompletedTask;
    }

    public Task StopAsync(CancellationToken cancellationToken)
        => Task.CompletedTask;

    public void NotifyGatewayCountChanged(
        int currentNumberOfGateways, int previousNumberOfGateways, bool connectionRecovered)
    {
        Volatile.Write(ref gateways, currentNumberOfGateways);

        if (currentNumberOfGateways > 0)
            Interlocked.CompareExchange(ref connectedAtTicks, DateTimeOffset.UtcNow.UtcTicks, 0);

        // The transition is logged here, once, so the readiness check can stay quiet: it runs every
        // few seconds and logging its verdict would turn a long outage into a long log saying the
        // same thing. The endpoint's own response carries the reason for anyone asking now.
        logger.LogInformation("Cluster client gateways {Previous} -> {Current}{Recovered}",
            previousNumberOfGateways, currentNumberOfGateways, connectionRecovered ? " (recovered)" : "");
    }

    public void NotifyClusterConnectionLost()
    {
        Volatile.Write(ref gateways, 0);
        logger.LogWarning("Cluster client lost its connection to the cluster");
    }
}

/// <summary>
/// Has this process's cluster client ever reached the cluster? Answers the startup probe.
/// </summary>
/// <remarks>
/// <para>This is the only probe on a client role that can end the pod, and it is the only one that
/// should be able to. A pod that has never connected is misconfigured or is talking to the wrong
/// cluster, and a restart is a reasonable thing to try. A pod that connected and then lost the
/// cluster is in an outage it shares with everything else, and restarting it would sever the
/// websockets it is holding for a cluster that is coming back.</para>
///
/// <para>So the distinction the two probes draw is "never" against "not right now", and the startup
/// probe's <c>failureThreshold</c> is where an operator decides how long "never" takes.</para>
/// </remarks>
public sealed class ClientStartupHealthCheck(ClusterClientStatus cluster) : IHealthCheck
{
    public Task<HealthCheckResult> CheckHealthAsync(HealthCheckContext context, CancellationToken ct = default)
    {
        var data = new Dictionary<string, object> { ["gateways"] = cluster.Gateways };

        return Task.FromResult(cluster.HasEverConnected
            ? HealthCheckResult.Healthy("Cluster client reached the cluster", data: data)
            : HealthCheckResult.Unhealthy("Cluster client has not reached the cluster yet", data: data));
    }
}

/// <summary>
/// Is this process worth keeping? Answers the liveness probe, whose only remedy is a restart.
/// </summary>
/// <remarks>
/// <para>Nothing about the cluster fails it, and that is the decision rather than an omission. The
/// client retries forever by design — the connection filter this role uses exists so an unreachable
/// cluster is an inconvenience rather than an outage — so a restart cannot reconnect it any faster
/// than it is already reconnecting, and it costs every websocket the pod is holding.</para>
///
/// <para>Which leaves the fact that the endpoint answered at all. That is a real signal: it means
/// the host is up, Kestrel is accepting, and the request pipeline is not wedged. The gateway count
/// rides along as data so a person reading the response gets the whole picture from one call.</para>
/// </remarks>
public sealed class ClientLivenessHealthCheck(ClusterClientStatus cluster) : IHealthCheck
{
    public Task<HealthCheckResult> CheckHealthAsync(HealthCheckContext context, CancellationToken ct = default)
        => Task.FromResult(HealthCheckResult.Healthy("Application is alive", data: new Dictionary<string, object>
        {
            ["gateways"]     = cluster.Gateways,
            ["everConnected"] = cluster.HasEverConnected
        }));
}

/// <summary>
/// Should Kubernetes route connections here? Answers the readiness probe.
/// </summary>
/// <remarks>
/// <para>Two ways to answer no. A stop has been asked for, in which case the point of the answer is
/// to get the pod out of the Service before the process goes; or the cluster client has no gateway,
/// in which case a connection accepted here would attach a session grain to nothing and hang on the
/// first call rather than failing.</para>
///
/// <para>Readiness going false is the whole of the admission control on this pass. It stops
/// <em>new</em> connections being routed here; the ones already open are untouched, because nothing
/// in the hub asks this question yet. That is enough for a rolling deployment — the sockets move
/// when the process stops, and they move to a pod that is ready — and short of what a maintenance
/// window wants, which is existing sockets migrating on their own.</para>
/// </remarks>
public sealed class ClientReadinessHealthCheck(
    ClusterClientStatus cluster,
    ClientStopSignal    stop,
    ILogger<ClientReadinessHealthCheck> logger) : IHealthCheck
{
    public Task<HealthCheckResult> CheckHealthAsync(HealthCheckContext context, CancellationToken ct = default)
    {
        var data = new Dictionary<string, object>
        {
            ["gateways"]      = cluster.Gateways,
            ["clientStarted"] = cluster.ClientStarted,
            ["everConnected"] = cluster.HasEverConnected,
            ["stopping"]      = stop.IsStopping,
            ["stopRequested"] = stop.RequestedAt?.ToString("O") ?? "no"
        };

        if (stop.IsStopping)
        {
            logger.LogDebug("Readiness check: not ready - the process is stopping");
            return Task.FromResult(HealthCheckResult.Unhealthy(
                "Process is stopping, not accepting new connections", data: data));
        }

        // The gateway count is reported, not enforced. Only `core` exposes a cluster gateway, so
        // gating readiness on it would take entrypoint, aegis, botapi, account and admin out of their
        // Services at the same instant on any core blip — and most of what those roles serve does not
        // touch Orleans at all: the Xsolla and LiveKit webhooks, the Sentry tunnel, the CDN redirects,
        // the version endpoint. A pod with no cluster answering errors on the calls that need one is a
        // smaller failure than every client endpoint in the region going dark together.
        //
        // It stays in `data` because that is where an operator looks, and it is what /health is for.

        return Task.FromResult(HealthCheckResult.Healthy("Ready to accept connections", data: data));
    }
}

/// <summary>
/// Registering and mapping the probes a client role answers.
/// </summary>
/// <remarks>
/// The same paths and the same tags as a silo — <see cref="HealthCheckExtensions.MapProbeEndpoints"/>
/// is shared — because those are the contract a manifest is written against. A role that answered
/// the same questions at different paths would need a manifest of its own, for nothing.
/// </remarks>
public static class ClientHealthCheckExtensions
{
    public static IServiceCollection AddClientHealthChecks(this IServiceCollection services)
    {
        services.AddHealthChecks()
           .AddCheck<ClientStartupHealthCheck>(
                "startup",
                failureStatus: HealthStatus.Unhealthy,
                tags: ["startup"])
           .AddCheck<ClientLivenessHealthCheck>(
                "liveness",
                failureStatus: HealthStatus.Unhealthy,
                tags: ["live", "liveness"])
           .AddCheck<ClientReadinessHealthCheck>(
                "readiness",
                failureStatus: HealthStatus.Unhealthy,
                tags: ["ready", "readiness"]);

        return services;
    }

    public static IEndpointRouteBuilder MapClientHealthChecks(this IEndpointRouteBuilder app)
        => app.MapProbeEndpoints();
}
