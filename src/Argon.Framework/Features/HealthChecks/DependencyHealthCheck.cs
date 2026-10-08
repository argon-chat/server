namespace Argon.HealthChecks;

using Microsoft.Extensions.Diagnostics.HealthChecks;

/// <summary>
/// A check against something outside the process, bounded in time and never throwing.
/// </summary>
/// <remarks>
/// <para>The bound is the part that matters. The clients these checks go through were configured for
/// a process that would rather wait than fail — NATS gets a minute to connect, Npgsql fifteen
/// seconds, StackExchange.Redis whatever the connection string says — and a probe inheriting those
/// waits is answered after Kubernetes has stopped listening. So the probe's own timeout is applied
/// here, once, and applied twice over: through the token, for the clients that honour one, and
/// through <see cref="Task.WaitAsync(CancellationToken)"/>, for the ones that do not (LiveKit's
/// client takes no token at all). The underlying call may run on after the probe has answered; that
/// is a cost worth one leaked connect attempt.</para>
///
/// <para>Never throwing is the other half. The health check service would catch an exception and
/// report it, but as the registration's failure status with the exception's type in the
/// description; putting the message first is what makes <c>/health</c> readable at three in the
/// morning.</para>
/// </remarks>
public abstract class DependencyHealthCheck(IOptions<ProbeOptions> options) : IHealthCheck
{
    public async Task<HealthCheckResult> CheckHealthAsync(HealthCheckContext context, CancellationToken cancellationToken = default)
    {
        var timeout = options.Value.Dependencies.Timeout;
        var watch   = Stopwatch.StartNew();

        using var bounded = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        bounded.CancelAfter(timeout);

        try
        {
            return await ProbeAsync(bounded.Token).WaitAsync(bounded.Token);
        }
        catch (OperationCanceledException) when (!cancellationToken.IsCancellationRequested)
        {
            return HealthCheckResult.Unhealthy(
                $"{context.Registration.Name} did not answer within {timeout.TotalSeconds:0.#} s",
                data: Timing(watch, timeout));
        }
        catch (Exception e)
        {
            return HealthCheckResult.Unhealthy($"{context.Registration.Name}: {e.Message}", e, Timing(watch, timeout));
        }
    }

    /// <summary>
    /// One round trip to the dependency. Throw on failure, or return an unhealthy result with a
    /// better message than the exception would carry.
    /// </summary>
    protected abstract Task<HealthCheckResult> ProbeAsync(CancellationToken ct);

    private static Dictionary<string, object> Timing(Stopwatch watch, TimeSpan timeout)
        => new()
        {
            ["elapsedMs"] = Math.Round(watch.Elapsed.TotalMilliseconds, 1),
            ["timeout"]   = timeout.ToString()
        };
}

/// <summary>
/// Registration of the dependency checks, and the tag the probes find them by.
/// </summary>
/// <remarks>
/// A feature that opens a connection registers its check here, from its own <c>Configure</c>, so a
/// role only ever probes what it actually uses: a client role with no database feature has no
/// database check to fail. Which probes the check may fail is not decided here — every check
/// carries the one tag, and the probe endpoints apply <see cref="ProbeOptions"/> when they run.
/// </remarks>
public static class DependencyHealthCheckExtensions
{
    public const string Tag = "dependency";

    public static IServiceCollection AddDependencyCheck<TCheck>(this IServiceCollection services, string name)
        where TCheck : DependencyHealthCheck
    {
        services.AddHealthChecks()
           .AddCheck<TCheck>(name, failureStatus: HealthStatus.Unhealthy, tags: [Tag, name]);

        return services;
    }
}

/// <summary>
/// Which checks a probe runs, and what their results add up to.
/// </summary>
/// <remarks>
/// <para>The framework's own endpoint cannot express this. It filters registrations by a predicate
/// and maps the report's aggregate status to a status code, and the aggregate is the worst entry —
/// so a dependency that is meant to soften to <c>Degraded</c> on readiness would still take the
/// probe to <c>503</c>. Two registrations per check with different failure statuses would work and
/// would double every entry on <c>/health</c>. Doing the arithmetic here keeps one registration per
/// dependency and lets each probe read it its own way.</para>
///
/// <para>Only dependency entries are softened. The probe's own check — has the silo joined, is it
/// draining, is the process stopping — says <c>Unhealthy</c> and means it.</para>
/// </remarks>
public static class ProbePolicy
{
    public static string TagOf(ProbeKind probe)
        => probe switch
        {
            ProbeKind.Startup   => "startup",
            ProbeKind.Readiness => "readiness",
            ProbeKind.Liveness  => "liveness",
            _                   => throw new ArgumentOutOfRangeException(nameof(probe), probe, null)
        };

    /// <summary>Whether a probe runs this registration at all.</summary>
    public static bool Includes(HealthCheckRegistration registration, ProbeKind probe, DependencyProbeOptions policy)
        => registration.Tags.Contains(TagOf(probe))
        || (registration.Tags.Contains(DependencyHealthCheckExtensions.Tag)
            && policy.GateFor(probe, registration.Name) is not ProbeGate.Off);

    /// <summary>
    /// The report as the probe reads it: the same entries, with an aggregate status that softens a
    /// dependency's failure where the policy says to.
    /// </summary>
    public static HealthReport Judge(HealthReport report, ProbeKind probe, DependencyProbeOptions policy)
    {
        var status = HealthStatus.Healthy;

        foreach (var (name, entry) in report.Entries)
        {
            var verdict = entry.Status;

            if (verdict is HealthStatus.Unhealthy
             && entry.Tags.Contains(DependencyHealthCheckExtensions.Tag)
             && policy.GateFor(probe, name) is ProbeGate.Degrade)
                verdict = HealthStatus.Degraded;

            // Unhealthy < Degraded < Healthy in the enum, so the worst entry is the smallest.
            if (verdict < status)
                status = verdict;
        }

        return new HealthReport(report.Entries, status, report.TotalDuration);
    }
}
