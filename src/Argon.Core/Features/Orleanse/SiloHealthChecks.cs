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
            .AddCheck<OrleansClusterHealthCheck>(
                "orleans-cluster",
                failureStatus: HealthStatus.Degraded,
                tags: ["ready", "orleans", "cluster"]);

        return services;
    }

    public static IEndpointRouteBuilder MapSiloHealthChecks(this IEndpointRouteBuilder app)
    {
        // Kubernetes startup probe - has the silo joined the cluster? Holds the other two off until
        // it passes, so a slow join is not mistaken for a wedged process.
        app.MapHealthChecks("/health/startup", new()
        {
            Predicate      = check => check.Tags.Contains("startup"),
            ResponseWriter = WriteHealthResponse
        });

        // Kubernetes liveness probe - is the app alive?
        app.MapHealthChecks("/health/live", new()
        {
            Predicate = check => check.Tags.Contains("liveness"),
            ResponseWriter = WriteHealthResponse
        });

        // Kubernetes readiness probe - can the app accept traffic?
        app.MapHealthChecks("/health/ready", new()
        {
            Predicate = check => check.Tags.Contains("readiness"),
            ResponseWriter = WriteHealthResponse
        });

        // Detailed health status for monitoring
        app.MapHealthChecks("/health", new()
        {
            ResponseWriter = WriteHealthResponse
        });

        return app;
    }

    private static async Task WriteHealthResponse(HttpContext context, HealthReport report)
    {
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
