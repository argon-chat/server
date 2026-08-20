namespace Argon.Features.EF;

using Microsoft.Extensions.DependencyInjection.Extensions;
using Microsoft.Extensions.Diagnostics.HealthChecks;
using System.Diagnostics.Metrics;

/// <summary>What the last pass concluded. Ordered so that the worst of several is the largest.</summary>
public enum SchemaReconcileVerdict
{
    /// <summary>No pass has run in this process.</summary>
    NotRun,

    /// <summary>There is nothing here to reconcile: the engine is PostgreSQL, or the mode is <c>Off</c>.</summary>
    NotApplicable,

    /// <summary>Every declared table matches, and every read succeeded.</summary>
    Converged,

    /// <summary>A statement ran and the pass finished. Kept apart from <see cref="Converged"/> so a deploy that changed something is visible in the metric.</summary>
    Applied,

    /// <summary>Another worker holds the lease, or holds the migration lock. This pod knows nothing, which is not the same as nothing being wrong.</summary>
    SkippedLock,

    /// <summary>Differences this actor could have closed and did not, because the mode is <c>Report</c>.</summary>
    Drift,

    /// <summary>Drift the current actor is not allowed to close — an operator has to run it.</summary>
    AwaitingApproval,

    /// <summary>Drift no flag enables, in any mode.</summary>
    Refused,

    /// <summary>The state could not be read. The verdict that looks like success and is not.</summary>
    Undetermined,

    /// <summary>A statement was issued and the server rejected it.</summary>
    Failed
}

/// <summary>The whole of what one pass found, kept for the health endpoint and the metrics.</summary>
public sealed record SchemaReconcileReport(
    SchemaReconcileVerdict Verdict,
    string Description,
    SchemaTtlPlan Plan,
    IReadOnlyList<string> Applied,
    DateTimeOffset At)
{
    public static readonly SchemaReconcileReport NotRun = new(
        SchemaReconcileVerdict.NotRun, "no schema reconcile pass has run in this process",
        SchemaTtlPlan.Empty, [], DateTimeOffset.MinValue);

    public static SchemaReconcileReport NotApplicable(string why)
        => new(SchemaReconcileVerdict.NotApplicable, why, SchemaTtlPlan.Empty, [], DateTimeOffset.UtcNow);

    /// <summary>
    /// A pass that threw instead of returning a verdict.
    /// </summary>
    /// <remarks>
    /// <see cref="SchemaReconcileVerdict.Undetermined"/> rather than
    /// <see cref="SchemaReconcileVerdict.Failed"/>: a statement the server rejected comes back as a
    /// verdict, so an exception that escapes the pass is almost always a read that did not happen —
    /// a catalog that could not be queried, a payload that no longer parses, a connection that went
    /// away. Reporting it as a failed <em>change</em> would claim knowledge of the database that this
    /// process does not have, and "could not look" is exactly what Undetermined is for.
    /// </remarks>
    public static SchemaReconcileReport Faulted(Exception e)
        => new(SchemaReconcileVerdict.Undetermined,
            $"the pass threw before reaching a verdict: {e.Message}",
            SchemaTtlPlan.Empty, [], DateTimeOffset.UtcNow);
}

/// <summary>
/// The last verdict, held for whoever asks — and nobody asks the database.
/// </summary>
/// <remarks>
/// <para>Cached rather than computed on demand, because the thing that asks is an HTTP endpoint. A
/// check that queried would turn a loopback scrape into a <c>SHOW CREATE TABLE</c> storm against the
/// production cluster, and a monitoring system that scrapes every fifteen seconds would keep it there.</para>
///
/// <para>The store behind it is static, and that is not a shortcut. Meter instruments are created once
/// per process and have no way to reach a container-resolved object, so the observable gauge needs a
/// static to read; there is exactly one reconciler per process, so a static and the singleton are the
/// same value rather than two sources of truth. The instance exists to give the health check something
/// to take in its constructor.</para>
/// </remarks>
public sealed class SchemaReconcileState
{
    private static SchemaReconcileReport latest = SchemaReconcileReport.NotRun;

    public SchemaReconcileReport Report => Volatile.Read(ref latest);

    public void Publish(SchemaReconcileReport report)
    {
        Volatile.Write(ref latest, report);

        SchemaReconcileInstruments.Passes.Add(1,
            new KeyValuePair<string, object?>("outcome", report.Verdict.ToString()));
    }

    /// <summary>
    /// One series per declared table, carrying whether it currently differs.
    /// </summary>
    /// <remarks>
    /// A converged table reports <c>0</c> rather than reporting nothing, so its series stays alive
    /// between passes. A gauge whose series vanish when everything is fine cannot be alerted on
    /// without the alert firing on the absence, and drift during a rollout is normal enough that such
    /// an alert would be muted within a week.
    /// </remarks>
    internal static IEnumerable<Measurement<long>> ObserveDrift()
        => Volatile.Read(ref latest).Plan.Items.Select(item => new Measurement<long>(
            item.Status is SchemaTtlStatus.Drift or SchemaTtlStatus.Undetermined ? 1 : 0,
            new KeyValuePair<string, object?>("table", item.Table.ToString()),
            new KeyValuePair<string, object?>("tier", item.Tier.ToString())));
}

/// <summary>Instruments for the reconcile pass, on the shared <see cref="Instruments.Meter"/>.</summary>
/// <remarks>
/// Names follow the documented <c>argon-{feature}-{metric}</c> convention. Alert on
/// <c>outcome=Undetermined</c> at all, and on <c>Refused</c> or <c>AwaitingApproval</c> persisting past
/// a deploy window — never on drift alone, which is normal while a rollout is in flight.
/// </remarks>
internal static class SchemaReconcileInstruments
{
    public static readonly Counter<long> Passes =
        Instruments.Meter.CreateCounter<long>(
            "argon-schema-reconcile-passes",
            unit: "{pass}",
            description: "Schema reconcile passes, tagged with the verdict they reached");

    public static readonly Histogram<double> Duration =
        Instruments.Meter.CreateHistogram<double>(
            "argon-schema-reconcile-duration",
            unit: "ms",
            description: "Wall-clock duration of a schema reconcile pass");

    public static readonly ObservableGauge<long> DriftItems =
        Instruments.Meter.CreateObservableGauge(
            "argon-schema-drift-items",
            observeValues: SchemaReconcileState.ObserveDrift,
            unit: "{table}",
            description: "Whether each declared table currently differs from its declaration");
}

/// <summary>
/// What the schema reconciler last found, for a person or a dashboard.
/// </summary>
/// <remarks>
/// <para><b>Tagged <c>diagnostic</c>, and it must stay that way.</b> <c>MapProbeEndpoints</c> filters
/// the three Kubernetes endpoints on <c>startup</c> / <c>liveness</c> / <c>readiness</c>, so a
/// <c>diagnostic</c> check never runs on a probe. Three reasons it must not: readiness answers "should
/// traffic come here", and a background schema change does not change that answer;
/// <c>deploy/k8s-probes.md</c> sets <c>failureThreshold: 1</c> on silo readiness, and every pod runs
/// this same pass, so a readiness-affecting verdict would take every pod out of the Service at the same
/// instant; and liveness is worse still, because its only remedy is a restart while a CockroachDB
/// schema change runs in the cluster and survives one — you would lose the observer and keep the work.</para>
///
/// <para>The full item list goes in <c>data</c>, which <c>WriteHealthResponse</c> already refuses to
/// emit to anything but a loopback caller.</para>
///
/// <para>Never <c>Unhealthy</c>. There is no verdict this check can reach where killing or draining the
/// pod is the right response.</para>
/// </remarks>
public sealed class SchemaReconcileHealthCheck(SchemaReconcileState state) : IHealthCheck
{
    public Task<HealthCheckResult> CheckHealthAsync(HealthCheckContext context, CancellationToken ct = default)
    {
        var report = state.Report;

        var data = new Dictionary<string, object>
        {
            ["verdict"] = report.Verdict.ToString(),
            ["at"]      = report.At == DateTimeOffset.MinValue ? "never" : report.At.ToString("O"),
            ["applied"] = report.Applied,
            ["items"] = report.Plan.Items.Select(item => new
            {
                table     = item.Table.ToString(),
                status    = item.Status.ToString(),
                tier      = item.Tier.ToString(),
                reason    = item.Reason,
                statement = item.Statement,
                notes     = item.Notes
            }).ToList()
        };

        // NotRun is healthy on purpose. It says "this process has not looked", which is not a claim
        // that anything is converged — and the roles that never look are the ones with no database
        // work to do. A diagnostic that is degraded by default on every such pod is a diagnostic
        // people stop reading, which costs more than it buys. The dangerous verdict is Undetermined,
        // and that one is degraded.
        var healthy = report.Verdict is SchemaReconcileVerdict.NotRun
                                     or SchemaReconcileVerdict.NotApplicable
                                     or SchemaReconcileVerdict.Converged
                                     or SchemaReconcileVerdict.Applied;

        return Task.FromResult(healthy
            ? HealthCheckResult.Healthy(report.Description, data: data)
            : HealthCheckResult.Degraded(report.Description, data: data));
    }
}

public static class SchemaReconcileDiagnostics
{
    /// <summary>
    /// Registers the verdict cache and the diagnostic health check.
    /// </summary>
    /// <remarks>
    /// Registered for every engine rather than only for CockroachDB. On PostgreSQL the pass publishes
    /// <see cref="SchemaReconcileVerdict.NotApplicable"/> and the endpoint says so — which is the point:
    /// a reconciler that is silent on an engine it cannot act on is indistinguishable from one that is
    /// broken, and the whole design refuses to let "could not look" read as "converged".
    /// </remarks>
    public static IServiceCollection AddSchemaReconcileDiagnostics(this IServiceCollection services)
    {
        services.TryAddSingleton<SchemaReconcileState>();

        services.AddHealthChecks()
           .AddCheck<SchemaReconcileHealthCheck>(
                "schema-ttl",
                failureStatus: HealthStatus.Degraded,
                tags: ["diagnostic", "schema", "ttl"]);

        return services;
    }
}
