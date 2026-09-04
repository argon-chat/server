namespace Argon.HealthChecks;

using Drains;
using Microsoft.Extensions.Diagnostics.HealthChecks;
using Orleans.Runtime;
using OrleansSiloStatus = SiloStatus;

/// <summary>
/// Is this process worth keeping? Answers Kubernetes' liveness probe, whose only remedy is a restart.
/// </summary>
/// <remarks>
/// <para>Deliberately blind to the drain. A draining silo is doing exactly what it was asked to do,
/// and reporting it as not alive would have Kubernetes restart the pod in the middle of handing its
/// grains over — losing the calls the drain exists to preserve. "Not taking traffic" is the readiness
/// probe's answer, not this one's.</para>
///
/// <para>Dead is the one state worth a restart: the cluster has written this silo off while the process
/// is still running, so it will never serve anything again on its own.</para>
/// </remarks>
public class LivenessHealthCheck(
    ISiloStatusOracle siloStatusOracle,
    ILogger<LivenessHealthCheck> logger) : IHealthCheck
{
    public Task<HealthCheckResult> CheckHealthAsync(
        HealthCheckContext context,
        CancellationToken ct = default)
    {
        try
        {
            var siloStatus = siloStatusOracle.CurrentStatus;

            // Silo is dead - unhealthy
            if (siloStatus == OrleansSiloStatus.Dead)
            {
                logger.LogWarning("Liveness check failed: Silo is Dead");
                return Task.FromResult(HealthCheckResult.Unhealthy(
                    "Silo is dead",
                    data: new Dictionary<string, object> { ["siloStatus"] = siloStatus.ToString() }));
            }

            // Any other status means the app is alive
            return Task.FromResult(HealthCheckResult.Healthy(
                "Application is alive",
                data: new Dictionary<string, object> { ["siloStatus"] = siloStatus.ToString() }));
        }
        catch (Exception ex)
        {
            logger.LogError(ex, "Liveness check failed with exception");
            return Task.FromResult(HealthCheckResult.Unhealthy(
                "Liveness check failed",
                ex));
        }
    }
}

/// <summary>
/// Readiness health check - verifies the silo is ready to accept traffic.
/// Returns not ready when draining or when Orleans silo is not Active.
/// </summary>
public class ReadinessHealthCheck(
    ISiloDrainService drainService,
    ISiloStatusOracle siloStatusOracle,
    IClusterMembershipService clusterMembership,
    ILogger<ReadinessHealthCheck> logger) : IHealthCheck
{
    public Task<HealthCheckResult> CheckHealthAsync(
        HealthCheckContext context,
        CancellationToken ct = default)
    {
        try
        {
            var drainStatus = drainService.GetStatus();
            var siloStatus = siloStatusOracle.CurrentStatus;
            var clusterSnapshot = clusterMembership.CurrentSnapshot;

            var data = new Dictionary<string, object>
            {
                ["drainState"] = drainStatus.State.ToString(),
                ["siloStatus"] = siloStatus.ToString(),
                ["activeGrains"] = drainStatus.ActiveGrainCount,
                ["clusterVersion"] = clusterSnapshot.Version.ToString(),
                ["activeSilos"] = clusterSnapshot.Members.Count(m => m.Value.Status == OrleansSiloStatus.Active)
            };

            // Check 1: Draining - not ready immediately when drain starts
            if (drainStatus.State != SiloDrainState.Active)
            {
                logger.LogInformation("Readiness check: Not ready - drain state is {State}", drainStatus.State);
                return Task.FromResult(HealthCheckResult.Unhealthy(
                    $"Silo is {drainStatus.State}, not accepting traffic",
                    data: data));
            }

            // Check 2: Orleans silo status must be Active
            if (siloStatus != OrleansSiloStatus.Active)
            {
                logger.LogInformation("Readiness check: Not ready - silo status is {Status}", siloStatus);
                return Task.FromResult(HealthCheckResult.Unhealthy(
                    $"Silo status is {siloStatus}, not ready for traffic",
                    data: data));
            }

            // A cluster of one is still a cluster: being the only active silo is a reason to take
            // traffic, not to refuse it.
            return Task.FromResult(HealthCheckResult.Healthy(
                "Silo is ready to accept traffic",
                data: data));
        }
        catch (Exception ex)
        {
            logger.LogError(ex, "Readiness check failed with exception");
            return Task.FromResult(HealthCheckResult.Unhealthy(
                "Readiness check failed",
                ex));
        }
    }
}

/// <summary>
/// Has this silo finished joining the cluster? Answers Kubernetes' startup probe.
/// </summary>
/// <remarks>
/// <para>Separate from readiness even though it asks a subset of the same question, because the two
/// are used differently: Kubernetes calls this one until it passes once and then never again, and it
/// holds the liveness probe off until then. Without it, a silo that takes longer than the liveness
/// threshold to find the membership table gets restarted for being slow, and restarts into the same
/// race.</para>
///
/// <para>Joining is the whole of it — a silo that is Active is in the membership table, reachable by
/// the others, and can be given grains. Whether it should be given traffic is readiness' question,
/// and it answers differently the moment a drain starts.</para>
/// </remarks>
public class StartupHealthCheck(
    ISiloStatusOracle siloStatusOracle,
    ILogger<StartupHealthCheck> logger) : IHealthCheck
{
    public Task<HealthCheckResult> CheckHealthAsync(
        HealthCheckContext context,
        CancellationToken ct = default)
    {
        var siloStatus = siloStatusOracle.CurrentStatus;
        var data       = new Dictionary<string, object> { ["siloStatus"] = siloStatus.ToString() };

        if (siloStatus is OrleansSiloStatus.Active)
            return Task.FromResult(HealthCheckResult.Healthy("Silo joined the cluster", data: data));

        logger.LogInformation("Startup check: silo status is {Status}, still joining", siloStatus);

        return Task.FromResult(HealthCheckResult.Unhealthy(
            $"Silo status is {siloStatus}; it has not joined the cluster yet", data: data));
    }
}

/// <summary>
/// Orleans cluster health check - verifies cluster connectivity.
/// </summary>
public class OrleansClusterHealthCheck(
    IClusterMembershipService clusterMembership,
    ISiloStatusOracle siloStatusOracle,
    ILogger<OrleansClusterHealthCheck> logger) : IHealthCheck
{
    public Task<HealthCheckResult> CheckHealthAsync(
        HealthCheckContext context,
        CancellationToken ct = default)
    {
        try
        {
            var snapshot = clusterMembership.CurrentSnapshot;
            var localStatus = siloStatusOracle.CurrentStatus;

            var members = snapshot.Members
                .GroupBy(m => m.Value.Status)
                .ToDictionary(g => g.Key.ToString(), g => g.Count());

            var data = new Dictionary<string, object>
            {
                ["localSiloStatus"] = localStatus.ToString(),
                ["clusterVersion"] = snapshot.Version.ToString(),
                ["totalMembers"] = snapshot.Members.Count
            };

            foreach (var (status, count) in members)
            {
                data[$"silos_{status}"] = count;
            }

            // Check if local silo is part of the cluster
            if (localStatus == OrleansSiloStatus.Dead)
            {
                return Task.FromResult(HealthCheckResult.Unhealthy(
                    "Local silo is Dead",
                    data: data));
            }

            var activeSilos = members.GetValueOrDefault("Active", 0);
            
            if (activeSilos == 0)
            {
                return Task.FromResult(HealthCheckResult.Degraded(
                    "No active silos in cluster",
                    data: data));
            }

            return Task.FromResult(HealthCheckResult.Healthy(
                $"Cluster healthy with {activeSilos} active silos",
                data: data));
        }
        catch (Exception ex)
        {
            logger.LogError(ex, "Orleans cluster health check failed");
            return Task.FromResult(HealthCheckResult.Unhealthy(
                "Failed to check cluster health",
                ex));
        }
    }
}

/// <summary>
/// Extension methods for registering health checks.
/// </summary>
public static class HealthCheckExtensions
{
    public static IServiceCollection AddSiloHealthChecks(this IServiceCollection services)
    {
        services.AddHealthChecks()
            .AddCheck<StartupHealthCheck>(
                "startup",
                failureStatus: HealthStatus.Unhealthy,
                tags: ["startup"])
            .AddCheck<LivenessHealthCheck>(
                "liveness",
                failureStatus: HealthStatus.Unhealthy,
                tags: ["live", "liveness"])
            .AddCheck<ReadinessHealthCheck>(
                "readiness", 
                failureStatus: HealthStatus.Unhealthy,
                tags: ["ready", "readiness"])
            // Tagged as a diagnostic rather than as readiness, which is what it was tagged as and
            // never behaved as: it carried "ready" while the readiness endpoint filters on
            // "readiness", so it has never run on a probe. Left that way deliberately — its only
            // unhealthy verdicts are "the local silo is Dead" and "reading membership threw", and
            // ReadinessHealthCheck already covers both. Making it gate readiness would add a way to
            // flap and no signal.
            .AddCheck<OrleansClusterHealthCheck>(
                "orleans-cluster",
                failureStatus: HealthStatus.Degraded,
                tags: ["diagnostic", "orleans", "cluster"]);

        return services;
    }

    public static IEndpointRouteBuilder MapSiloHealthChecks(this IEndpointRouteBuilder app)
        => app.MapProbeEndpoints();

    /// <summary>
    /// The four endpoints, and the tags each one filters on.
    /// </summary>
    /// <remarks>
    /// <para>Shared with the client roles' checks rather than written twice: the paths and the tag
    /// names are the contract a Kubernetes manifest is written against, and a role that answered the
    /// same questions somewhere else would need a manifest of its own for no reason. What differs
    /// between a silo and a client is which checks carry the tags, not where they are served.</para>
    ///
    /// <para>Each probe also runs the dependency checks the role's features registered — database,
    /// NATS, Redis, object storage, Vault, the SFU — as <see cref="ProbeOptions"/> says it should:
    /// the startup probe fails on them, readiness reports them, liveness ignores them. The probes are
    /// mapped by hand rather than through <c>MapHealthChecks</c> because that arithmetic is not one
    /// its options can express; see <see cref="ProbePolicy"/>.</para>
    /// </remarks>
    internal static IEndpointRouteBuilder MapProbeEndpoints(this IEndpointRouteBuilder app)
    {
        // Kubernetes startup probe - has this process reached the cluster, and can it reach what it
        // needs? Holds the other two off until it passes, so a slow join is not mistaken for a
        // wedged process — and a pod that cannot reach its dependencies never passes it, which is
        // what keeps a rollout from promoting it.
        app.MapProbe("/health/startup", ProbeKind.Startup);

        // Kubernetes liveness probe - is the app alive?
        app.MapProbe("/health/live", ProbeKind.Liveness);

        // Kubernetes readiness probe - can the app accept traffic?
        app.MapProbe("/health/ready", ProbeKind.Readiness);

        // Detailed health status for monitoring: every check, dependencies and diagnostics included.
        app.MapHealthChecks("/health", new()
        {
            ResponseWriter = WriteHealthResponse
        });

        return app;
    }

    private static void MapProbe(this IEndpointRouteBuilder app, string path, ProbeKind probe)
        => app.MapGet(path, async (HttpContext http, HealthCheckService health, IOptions<ProbeOptions> options) =>
        {
            var policy = options.Value.Dependencies;

            var report  = await health.CheckHealthAsync(
                registration => ProbePolicy.Includes(registration, probe, policy), http.RequestAborted);
            var verdict = ProbePolicy.Judge(report, probe, policy);

            http.Response.StatusCode = verdict.Status is HealthStatus.Unhealthy
                ? StatusCodes.Status503ServiceUnavailable
                : StatusCodes.Status200OK;

            // What the framework's own endpoint sets, kept: a cached probe answer is no answer.
            http.Response.Headers.CacheControl = "no-store, no-cache";
            http.Response.Headers.Pragma       = "no-cache";
            http.Response.Headers.Expires      = "Thu, 01 Jan 1970 00:00:00 GMT";

            await WriteProbeResponse(http, verdict);
        });

    /// <summary>
    /// What a probe gets: the status code, and one word so a human curling it sees something.
    /// </summary>
    /// <remarks>
    /// Kubernetes reads the status code and throws the body away, so there is nothing to lose here and
    /// something to gain: on a silo these endpoints sit on an internal port, but on a client role they
    /// are served by the same Kestrel listener as api.argon.gl. The detailed body names every check and
    /// dumps its whole data dictionary — for a client that is the region's gateway count, whether this
    /// pod is mid-shutdown, and any exception message a failing check produced.
    /// </remarks>
    public static Task WriteProbeResponse(HttpContext context, HealthReport report)
    {
        context.Response.ContentType = "text/plain";
        return context.Response.WriteAsync(report.Status.ToString());
    }

    /// <summary>
    /// The detailed report, for a person or a dashboard — and only for one that is on the box.
    /// </summary>
    /// <remarks>
    /// <para>Same reasoning and same guard as the pre-stop hook. Anything scraping this either runs
    /// beside the pod or arrives through something that terminates the request and re-originates it,
    /// which is exactly the traffic a loopback check keeps out. An anonymous caller from the internet
    /// gets what a probe gets.</para>
    ///
    /// <para>Guarded in the writer rather than with an endpoint filter on purpose: filters run inside
    /// the route-handler pipeline, and <c>MapHealthChecks</c> does not build one — an
    /// <c>AddEndpointFilter</c> here would attach metadata nothing ever reads and silently do
    /// nothing.</para>
    /// </remarks>
    public static async Task WriteHealthResponse(HttpContext context, HealthReport report)
    {
        var ip = context.Connection.RemoteIpAddress;

        if (ip is null || !IPAddress.IsLoopback(ip))
        {
            await WriteProbeResponse(context, report);
            return;
        }

        context.Response.ContentType = "application/json";

        var response = new
        {
            status = report.Status.ToString(),
            duration = report.TotalDuration.TotalMilliseconds,
            checks = report.Entries.Select(e => new
            {
                name = e.Key,
                status = e.Value.Status.ToString(),
                description = e.Value.Description,
                duration = e.Value.Duration.TotalMilliseconds,
                data = e.Value.Data,
                exception = e.Value.Exception?.Message
            })
        };

        await context.Response.WriteAsJsonAsync(response);
    }
}
