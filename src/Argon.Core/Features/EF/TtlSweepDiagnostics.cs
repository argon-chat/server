namespace Argon.Features.EF;

using Microsoft.Extensions.DependencyInjection.Extensions;
using Microsoft.Extensions.Diagnostics.HealthChecks;
using System.Diagnostics.Metrics;

/// <summary>Where one table stands after a pass.</summary>
public enum TtlSweepStatus
{
    /// <summary>Nothing was expired. The steady state, and the one this should reach most passes.</summary>
    Clean,

    /// <summary>Rows match and the mode is <see cref="TtlSweepMode.Report"/>, so none were touched.</summary>
    Reported,

    /// <summary>Rows matched and were deleted.</summary>
    Swept,

    /// <summary>
    /// The table will not be swept, in any mode, and no configuration turns it on.
    /// </summary>
    /// <remarks>
    /// Reported on every pass rather than logged once and forgotten. A refusal nobody can see is
    /// indistinguishable from a sweeper that did not notice the table, and the two want opposite
    /// responses from whoever reads the report.
    /// </remarks>
    Refused,

    /// <summary>The server rejected a statement, or would not answer the count.</summary>
    Failed
}

/// <summary>One table's outcome.</summary>
/// <param name="Matched">
/// How many rows the predicate claimed, or <c>null</c> when nobody counted — which is every table in
/// <see cref="TtlSweepMode.Apply"/>, where the deletes themselves are the count and a separate scan
/// would only be a second pass over the same rows.
/// </param>
/// <param name="AtLeast">
/// <c>true</c> when the count hit its cap, so <paramref name="Matched"/> is a lower bound. The
/// distinction is the difference between "1,000 rows to clear" and "at least 1,000, and nobody knows
/// how many more".
/// </param>
public sealed record TtlSweepItem(
    TableRef Table,
    TtlSweepStatus Status,
    string Reason,
    long? Matched,
    bool AtLeast,
    long Deleted,
    int Batches,
    bool BudgetExhausted,
    string? Statement)
{
    public static TtlSweepItem Refused(TtlSweepTarget target)
        => new(target.Table, TtlSweepStatus.Refused, target.Refusal!, null, false, 0, 0, false, null);
}

/// <summary>What a whole pass concluded. Ordered so the worst of several is the largest.</summary>
public enum TtlSweepVerdict
{
    /// <summary>No pass has run in this process.</summary>
    NotRun,

    /// <summary>Nothing here to do: the engine is CockroachDB, or the mode is <c>Off</c>.</summary>
    NotApplicable,

    /// <summary>Every declared table was reached and nothing was expired.</summary>
    Clean,

    /// <summary>Rows were deleted and the pass finished.</summary>
    Swept,

    /// <summary>Rows match and the mode is <c>Report</c>. Not a problem; a number waiting for a decision.</summary>
    Reported,

    /// <summary>Another worker holds the sweep lease, or lost it mid-pass. This pod knows nothing.</summary>
    SkippedLock,

    /// <summary>At least one declared table is refused, in every mode, with no flag that enables it.</summary>
    Refused,

    /// <summary>A statement was issued and the server rejected it.</summary>
    Failed
}

/// <summary>The whole of what one pass found.</summary>
public sealed record TtlSweepReport(
    TtlSweepVerdict Verdict,
    string Description,
    IReadOnlyList<TtlSweepItem> Items,
    DateTimeOffset At)
{
    public static readonly TtlSweepReport NotRun = new(
        TtlSweepVerdict.NotRun, "no TTL sweep has run in this process", [], DateTimeOffset.MinValue);

    public static TtlSweepReport NotApplicable(string why)
        => new(TtlSweepVerdict.NotApplicable, why, [], DateTimeOffset.UtcNow);

    /// <summary>Total rows this pass actually removed, across every table.</summary>
    public long Deleted => Items.Sum(item => item.Deleted);

    /// <summary>
    /// The verdict, which is the worst thing in the list.
    /// </summary>
    /// <remarks>
    /// <c>Failed</c> outranks <c>Refused</c> outranks everything else, and <c>Swept</c> is kept apart
    /// from <c>Clean</c> deliberately: a pass that deleted rows and a pass that found none are both
    /// healthy, but only one of them is worth looking at when somebody asks where the rows went.
    /// </remarks>
    public static TtlSweepReport From(IReadOnlyList<TtlSweepItem> items, string why)
    {
        var verdict = true switch
        {
            _ when items.Any(item => item.Status is TtlSweepStatus.Failed)   => TtlSweepVerdict.Failed,
            _ when items.Any(item => item.Status is TtlSweepStatus.Refused)  => TtlSweepVerdict.Refused,
            _ when items.Any(item => item.Status is TtlSweepStatus.Swept)    => TtlSweepVerdict.Swept,
            _ when items.Any(item => item.Status is TtlSweepStatus.Reported) => TtlSweepVerdict.Reported,
            _                                                                => TtlSweepVerdict.Clean
        };

        var deleted = items.Sum(item => item.Deleted);
        var pending = items.Where(item => item.Status is TtlSweepStatus.Reported).Sum(item => item.Matched ?? 0);

        var summary = verdict switch
        {
            TtlSweepVerdict.Swept    => $"deleted {deleted} expired row(s) across {items.Count} declared table(s)",
            TtlSweepVerdict.Reported => $"{pending} expired row(s) would be deleted across {items.Count} declared table(s)",
            _                        => $"{items.Count} declared table(s)"
        };

        return new TtlSweepReport(verdict, $"{summary} ({why})", items, DateTimeOffset.UtcNow);
    }

    /// <summary>
    /// A pass that threw instead of returning a verdict.
    /// </summary>
    /// <remarks>
    /// <c>Failed</c> rather than a quieter "could not look" verdict. The pass issues <c>DELETE</c>, so an
    /// exception escaping it can have left rows removed and the accounting incomplete, and that must not
    /// present as anything softer than a failure.
    /// </remarks>
    public static TtlSweepReport Faulted(Exception e)
        => new(TtlSweepVerdict.Failed, $"the sweep threw before reaching a verdict: {e.Message}", [], DateTimeOffset.UtcNow);
}

/// <summary>
/// The last verdict, held for whoever asks — and nobody asks the database.
/// </summary>
/// <remarks>
/// Cached because the caller is an HTTP endpoint and a check that queried would turn a loopback scrape
/// into a table scan; static because meter instruments are created once per process and cannot reach a
/// container-resolved object, and there is exactly one sweeper per process anyway.
/// </remarks>
public sealed class TtlSweepState
{
    private static TtlSweepReport latest = TtlSweepReport.NotRun;

    public TtlSweepReport Report => Volatile.Read(ref latest);

    public void Publish(TtlSweepReport report)
    {
        Volatile.Write(ref latest, report);

        TtlSweepInstruments.Passes.Add(1, new KeyValuePair<string, object?>("outcome", report.Verdict.ToString()));

        foreach (var item in report.Items.Where(item => item.Deleted > 0))
            TtlSweepInstruments.RowsDeleted.Add(item.Deleted, new KeyValuePair<string, object?>("table", item.Table.ToString()));
    }

    /// <summary>
    /// One series per declared table, carrying the backlog the last pass saw.
    /// </summary>
    /// <remarks>
    /// A table with nothing expired reports <c>0</c> rather than reporting nothing, so its series stays
    /// alive between passes — a gauge whose series vanish when everything is fine cannot be alerted on
    /// without the alert firing on the absence.
    /// </remarks>
    internal static IEnumerable<Measurement<long>> ObserveBacklog()
        => Volatile.Read(ref latest).Items.Select(item => new Measurement<long>(
            item.Matched ?? 0,
            new KeyValuePair<string, object?>("table", item.Table.ToString()),
            new KeyValuePair<string, object?>("status", item.Status.ToString())));
}

/// <summary>Instruments for the sweep, on the shared <see cref="Instruments.Meter"/>.</summary>
/// <remarks>
/// Names follow the documented <c>argon-{feature}-{metric}</c> convention. Alert on
/// <c>outcome=Failed</c>, and on a backlog that grows across passes — never on a backlog existing,
/// which is the normal state of a deployment running in the default report mode.
/// </remarks>
internal static class TtlSweepInstruments
{
    public static readonly Counter<long> Passes =
        Instruments.Meter.CreateCounter<long>(
            "argon-ttl-sweep-passes",
            unit: "{pass}",
            description: "TTL sweep passes, tagged with the verdict they reached");

    public static readonly Counter<long> RowsDeleted =
        Instruments.Meter.CreateCounter<long>(
            "argon-ttl-sweep-rows",
            unit: "{row}",
            description: "Expired rows deleted by the TTL sweeper, per table");

    public static readonly Histogram<double> Duration =
        Instruments.Meter.CreateHistogram<double>(
            "argon-ttl-sweep-duration",
            unit: "ms",
            description: "Wall-clock duration of a TTL sweep pass");

    public static readonly ObservableGauge<long> Backlog =
        Instruments.Meter.CreateObservableGauge(
            "argon-ttl-sweep-backlog",
            observeValues: TtlSweepState.ObserveBacklog,
            unit: "{row}",
            description: "Expired rows the last pass found waiting in each declared table");
}

/// <summary>
/// What the TTL sweeper last did, for a person or a dashboard.
/// </summary>
/// <remarks>
/// <para><b>Tagged <c>diagnostic</c>, and it must stay that way.</b> <c>MapProbeEndpoints</c> filters the
/// Kubernetes endpoints on <c>startup</c> / <c>liveness</c> / <c>readiness</c>, and a sweeper that
/// found something it will not delete says nothing at all about whether this pod should take traffic.
/// Silo readiness runs at <c>failureThreshold: 1</c>, and every pod would reach the same verdict at the
/// same instant.</para>
///
/// <para>Never <c>Unhealthy</c>. Restarting a pod does not clear a backlog and does not fix a
/// mis-declared TTL; it only loses the process that was reporting it.</para>
/// </remarks>
public sealed class TtlSweepHealthCheck(TtlSweepState state) : IHealthCheck
{
    public Task<HealthCheckResult> CheckHealthAsync(HealthCheckContext context, CancellationToken ct = default)
    {
        var report = state.Report;

        var data = new Dictionary<string, object>
        {
            ["verdict"] = report.Verdict.ToString(),
            ["at"]      = report.At == DateTimeOffset.MinValue ? "never" : report.At.ToString("O"),
            ["deleted"] = report.Deleted,
            ["items"] = report.Items.Select(item => new
            {
                table     = item.Table.ToString(),
                status    = item.Status.ToString(),
                reason    = item.Reason,
                matched   = item.Matched,
                atLeast   = item.AtLeast,
                deleted   = item.Deleted,
                batches   = item.Batches,
                exhausted = item.BudgetExhausted,
                statement = item.Statement
            }).ToList()
        };

        // Reported is healthy, and that is the deliberate part. Report is the default mode, so a
        // deployment that has never opted in sits on a backlog by design; degrading on it would make
        // this check red everywhere on day one and ignored by week two. Refused is degraded because it
        // names a declaration that is wrong, and Failed because the server refused a statement.
        var healthy = report.Verdict is TtlSweepVerdict.NotRun
                                     or TtlSweepVerdict.NotApplicable
                                     or TtlSweepVerdict.Clean
                                     or TtlSweepVerdict.Swept
                                     or TtlSweepVerdict.Reported
                                     or TtlSweepVerdict.SkippedLock;

        return Task.FromResult(healthy
            ? HealthCheckResult.Healthy(report.Description, data: data)
            : HealthCheckResult.Degraded(report.Description, data: data));
    }
}

public static class TtlSweepDiagnostics
{
    /// <summary>
    /// Registers the verdict cache and the diagnostic health check.
    /// </summary>
    /// <remarks>
    /// For every engine, not only PostgreSQL: the most useful thing this can say on CockroachDB is "not
    /// applicable, the database is doing it itself", and a diagnostic that only exists where it applies
    /// cannot say that.
    /// </remarks>
    public static IServiceCollection AddTtlSweepDiagnostics(this IServiceCollection services)
    {
        services.TryAddSingleton<TtlSweepState>();

        services.AddHealthChecks()
           .AddCheck<TtlSweepHealthCheck>(
                "ttl-sweep",
                failureStatus: HealthStatus.Degraded,
                tags: ["diagnostic", "ttl", "sweep"]);

        return services;
    }
}
