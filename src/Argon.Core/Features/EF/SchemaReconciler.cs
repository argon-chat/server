namespace Argon.Features.EF;

using Npgsql;
using System.Data.Common;

/// <summary>How much the reconciler is allowed to do.</summary>
public enum SchemaReconcileMode
{
    /// <summary>Nothing at all — not even the read. The kill switch.</summary>
    Off,

    /// <summary>
    /// Read, diff, log, change nothing. The default, and the dry run.
    /// </summary>
    /// <remarks>
    /// The first release can only tell you things. Production evidence that the diff is right is
    /// gathered before anything is allowed to act on it, because a reconciler that faithfully applies a
    /// wrong desired state is worse than no reconciler.
    /// </remarks>
    Report,

    /// <summary>
    /// Issue the statements the actor's tier allows. Never more than that, whatever this says.
    /// </summary>
    /// <remarks>
    /// Opt-in through configuration <em>and</em> bounded by the tier ceiling the caller passes, which
    /// on the boot path is <see cref="SchemaChangeTier.Automatic"/>. Turning this on does not enable
    /// <see cref="SchemaChangeTier.Approval"/> work on a pod boot; nothing enables
    /// <see cref="SchemaChangeTier.Refused"/> work anywhere.
    /// </remarks>
    Apply
}

/// <summary>Everything the reconciler reads from configuration.</summary>
/// <param name="LeaseLifetime">
/// How long a tenure survives without renewal. Minutes rather than seconds because the renewal is
/// explicit rather than a background heartbeat — see <see cref="SchemaReconcileLease"/>.
/// </param>
public sealed record SchemaReconcileOptions(SchemaReconcileMode Mode, TimeSpan LeaseLifetime)
{
    public const string ModeKey = "Database:Reconcile:Mode";

    /// <summary>
    /// Reads the mode, defaulting to <see cref="SchemaReconcileMode.Report"/>.
    /// </summary>
    /// <remarks>
    /// Unset and unparsable both land on <c>Report</c>, and the direction of that default is the
    /// opposite of <c>Database:Provider</c>'s on purpose: the engine key fails open towards CockroachDB
    /// because it only chose a generator, while this one decides whether a pod issues DDL against
    /// production. A typo in a config map must cost a log line, never an unattended <c>ALTER</c>.
    /// </remarks>
    public static SchemaReconcileOptions FromConfiguration(IConfiguration configuration)
        => new(
            Enum.TryParse<SchemaReconcileMode>(configuration[ModeKey], ignoreCase: true, out var mode)
                ? mode
                : SchemaReconcileMode.Report,
            TimeSpan.FromMinutes(2));
}

/// <summary>
/// Converges a live database's row-level TTL onto what the EF model declares — by reading both sides
/// and emitting <c>ALTER</c>, never by a migration.
/// </summary>
/// <remarks>
/// <para><b>Why TTL and not placement, given that placement is the defect everyone knows about.</b>
/// They have the identical bug: <c>MultiregionalMigrationsSqlGenerator</c> writes both clauses only
/// from its <c>CreateTableOperation</c> override, so an annotation added to a table that already exists
/// produces no migration operation and never reaches any database —
/// <c>DbLocalityTests.Changing_a_locality_after_the_table_exists_produces_nothing</c> pins that on
/// purpose. TTL is declared on three tables instead of eleven, and its statement is a descriptor edit
/// where a locality change moves replicas across a WAN. So the whole loop — desired state, observed
/// state, normalisation, diff, ordered plan, tiers, lease, dry-run, metrics — is built and proven here
/// first, on a change that cannot cost an index rewrite if it is wrong.</para>
///
/// <para><b>Level-triggered, and there is no transaction.</b> Every pass re-derives everything from the
/// catalog; no plan is stored and none is resumed. CockroachDB supports DDL inside an explicit
/// transaction only for <c>CREATE TABLE</c> and <c>CREATE INDEX</c>, and mixing anything else risks
/// <c>XXA00</c> — <em>transaction committed but schema change aborted … manual inspection may be
/// required</em> — which additionally <em>replaces</em> the schema change's own error with a generic
/// one, discarding the single thing that would have said what went wrong. So each statement is issued
/// alone, autocommitted, on a raw command rather than through EF: <c>AddPooledDatabase</c> configures
/// <c>EnableRetryOnFailure</c>, and a blind retry of a statement whose outcome is unknown is exactly
/// the failure this shape exists to avoid.</para>
/// </remarks>
public static class SchemaReconciler
{
    /// <summary>
    /// One pass. Reads, diffs, logs, and — only in <see cref="SchemaReconcileMode.Apply"/> and only up
    /// to <paramref name="ceiling"/> — issues statements.
    /// </summary>
    /// <remarks>
    /// <para><c>ceiling</c> is the highest tier this actor may run. The boot path passes
    /// <see cref="SchemaChangeTier.Automatic"/>; an operator-driven entry point would pass
    /// <see cref="SchemaChangeTier.Approval"/>. Nothing passes <see cref="SchemaChangeTier.Refused"/>,
    /// and the plan builder never attaches a statement to one, so there is no argument that unlocks it.</para>
    ///
    /// <para>The dry run is this same method with <see cref="SchemaReconcileMode.Report"/>: same reads,
    /// same diff, same log lines, one branch that does not issue. A separate "plan" implementation
    /// would let a green plan and a wrong apply coexist, which is the failure this whole exercise
    /// exists to avoid.</para>
    /// </remarks>
    public async static Task<SchemaReconcileReport> RunAsync(
        DbContext dbContext,
        DbConnection connection,
        SchemaReconcileOptions options,
        SchemaChangeTier ceiling,
        string roleId,
        ILogger logger,
        CancellationToken ct = default)
    {
        var started = Stopwatch.GetTimestamp();

        try
        {
            return await PassAsync(dbContext, connection, options, ceiling, roleId, logger, ct);
        }
        finally
        {
            SchemaReconcileInstruments.Duration.Record(Stopwatch.GetElapsedTime(started).TotalMilliseconds);
        }
    }

    private async static Task<SchemaReconcileReport> PassAsync(
        DbContext dbContext,
        DbConnection connection,
        SchemaReconcileOptions options,
        SchemaChangeTier ceiling,
        string roleId,
        ILogger logger,
        CancellationToken ct)
    {
        if (options.Mode is SchemaReconcileMode.Off)
        {
            logger.LogInformation("Schema TTL reconciler is off ({Key}=Off); nothing was read", SchemaReconcileOptions.ModeKey);
            return SchemaReconcileReport.NotApplicable($"{SchemaReconcileOptions.ModeKey} is Off");
        }

        // Probed, not declared. DatabaseEngineProbe is already the one place that asks the server what
        // it is, and asking again here costs one round trip and makes this callable from a path that
        // did not run VerifyAsync first — the operator CLI being the obvious one. The alternative,
        // trusting the DatabaseProvider singleton, is a value that resolves to CockroachDb when the key
        // is unset or misspelled, which for a thing that emits ALTER is a fail-open in the wrong
        // direction.
        if (await DatabaseEngineProbe.DetectAsync(connection, ct) is not DatabaseProviderKind.CockroachDb)
        {
            // Said out loud rather than skipped in silence: a reconciler that is quiet on an engine it
            // cannot act on is indistinguishable from one that is broken. Once per process, because
            // this runs once per process.
            logger.LogInformation(
                "Row-level TTL is CockroachDB syntax and this server is PostgreSQL; the schema TTL " +
                "reconciler has nothing to do here and read nothing");

            return SchemaReconcileReport.NotApplicable("the server is PostgreSQL; row-level TTL is CockroachDB-only");
        }

        var desired = SchemaTtlModel.ReadDesiredState(dbContext.Model);

        if (desired.Count == 0)
        {
            logger.LogInformation("The model declares no row-level TTL; nothing to reconcile");
            return new SchemaReconcileReport(SchemaReconcileVerdict.Converged,
                "the model declares no row-level TTL", SchemaTtlPlan.Empty, [], DateTimeOffset.UtcNow);
        }

        var applied = new List<string>();

        SchemaReconcileLease? lease = null;

        try
        {
            // One more pass than there are tables: every statement closes one table's drift, so a pass
            // that keeps finding work after that has issued something that does not converge. Stopping
            // and reporting Failed is the poisoned-change latch in its smallest form — the alternative
            // is a pod that re-issues a rejected ALTER on every iteration for as long as it runs.
            var budget = desired.Count + 1;

            for (var iteration = 0; iteration <= budget; iteration++)
            {
                var plan = SchemaTtlPlan.Build(desired, await ObserveAsync(connection, desired.Keys, ct));

                if (iteration == 0)
                    Describe(plan, options.Mode, ceiling, logger);

                if (options.Mode is not SchemaReconcileMode.Apply)
                    return Conclude(plan, applied, ceiling, "report only");

                var next      = plan.Runnable(ceiling).FirstOrDefault();
                var statement = next?.Statement;

                if (next is null || statement is null)
                    return Conclude(plan, applied, ceiling, "nothing left to run");

                // Two schema changes at once is how CockroachDB produces errors nobody can read, and
                // its own documentation says not to run more than one at a time in production. A read
                // that fails is deliberately not fatal: never applying because a privilege is missing
                // is a worse and quieter failure than not stacking, so the pass drops back to
                // report-only and says which capability it wanted.
                var inFlight = await CountInFlightSchemaChangesAsync(connection, logger, ct);

                if (inFlight is null)
                    return new SchemaReconcileReport(SchemaReconcileVerdict.Undetermined,
                        "could not read SHOW JOBS, so it is not known whether a schema change is already " +
                        "in flight; nothing was issued", plan, applied, DateTimeOffset.UtcNow);

                if (inFlight > 0)
                {
                    logger.LogInformation(
                        "{Count} schema change job(s) are already in flight; leaving this pass to the next boot",
                        inFlight);

                    return new SchemaReconcileReport(SchemaReconcileVerdict.SkippedLock,
                        $"{inFlight} schema change job(s) are in flight", plan, applied, DateTimeOffset.UtcNow);
                }

                // ct by name: the lease now takes the resource it protects between the lifetime and the
                // token, so that TtlSweeper can hold one against its own row rather than this one.
                lease ??= await SchemaReconcileLease.TryAcquireAsync(
                    connection, logger, roleId, options.LeaseLifetime, ct: ct);

                if (lease is null)
                    return new SchemaReconcileReport(SchemaReconcileVerdict.SkippedLock,
                        "another worker holds the schema reconcile lease", plan, applied, DateTimeOffset.UtcNow);

                // Renewed immediately before the statement, and a failure stops the pass. Losing the
                // lease is a correct outcome — it means somebody else is converging — and continuing
                // to issue DDL under a tenure that ended is not.
                if (!await lease.TryRenewAsync(ct))
                    return new SchemaReconcileReport(SchemaReconcileVerdict.SkippedLock,
                        "the schema reconcile lease was lost mid-pass", plan, applied, DateTimeOffset.UtcNow);

                logger.LogWarning(
                    "Applying schema TTL change to {Table} [{Tier}] as {Holder} fence {Fence}: {Sql} — {Reason}",
                    next.Table, next.Tier, lease.Holder, lease.Fence, statement, next.Reason);

                try
                {
                    await ExecuteAsync(connection, statement, ct);
                }
                catch (PostgresException e)
                {
                    // Never retried here. The statement's outcome is not knowable from the error alone
                    // — a timeout or a dropped connection leaves the background job running — so the
                    // only safe next step is to stop, report verbatim, and let the next pass re-read
                    // the catalog and decide from what is actually there.
                    logger.LogError(e, "CockroachDB refused {Sql} ({SqlState})", statement, e.SqlState);

                    return new SchemaReconcileReport(SchemaReconcileVerdict.Failed,
                        $"{next.Table}: {e.SqlState} {e.MessageText}", plan, applied, DateTimeOffset.UtcNow);
                }

                applied.Add(statement);
            }

            logger.LogError(
                "The schema TTL reconciler issued {Count} statement(s) and still finds work; stopping so it " +
                "does not re-issue a change that is not taking", applied.Count);

            return new SchemaReconcileReport(SchemaReconcileVerdict.Failed,
                "the plan did not converge; a statement is being accepted without taking effect",
                SchemaTtlPlan.Empty, applied, DateTimeOffset.UtcNow);
        }
        finally
        {
            if (lease is not null)
                await lease.DisposeAsync();
        }
    }

    /// <summary>The verdict, which is the worst thing in the plan and never better than what was read.</summary>
    private static SchemaReconcileReport Conclude(
        SchemaTtlPlan plan, IReadOnlyList<string> applied, SchemaChangeTier ceiling, string why)
    {
        var drift = plan.Items.Where(item => item.Status is SchemaTtlStatus.Drift).ToList();

        // Ordered from the verdict that must never be mistaken for success downwards. Undetermined
        // outranks everything: a pass that could not read one table has not established that the other
        // two are right, and letting a "converged" verdict out of that state is the one failure mode
        // section 4 singles out as the worst available.
        var verdict = true switch
        {
            _ when plan.HasUndetermined                                     => SchemaReconcileVerdict.Undetermined,
            _ when drift.Any(item => item.Tier is SchemaChangeTier.Refused) => SchemaReconcileVerdict.Refused,
            _ when drift.Any(item => item.Tier > ceiling)                   => SchemaReconcileVerdict.AwaitingApproval,
            _ when drift.Count > 0                                          => SchemaReconcileVerdict.Drift,
            _ when applied.Count > 0                                        => SchemaReconcileVerdict.Applied,
            _                                                               => SchemaReconcileVerdict.Converged
        };

        var drifting = plan.Items.Count(item => item.Status is SchemaTtlStatus.Drift);

        return new SchemaReconcileReport(verdict,
            drifting == 0
                ? $"every declared table matches ({why})"
                : $"{drifting} of {plan.Items.Count} declared table(s) differ ({why})",
            plan, applied, DateTimeOffset.UtcNow);
    }

    private async static Task<Dictionary<TableRef, TtlObservation>> ObserveAsync(
        DbConnection connection, IEnumerable<TableRef> tables, CancellationToken ct)
    {
        var observed = new Dictionary<TableRef, TtlObservation>();

        // Only the tables the model declares a TTL for — never "every table in the database". Reading
        // the whole catalog on every boot costs a statement per table for no signal, and the tables
        // nobody declared are exactly the ones this must not touch.
        foreach (var table in tables)
            observed[table] = await SchemaTtlCatalog.ReadAsync(connection, table, ct);

        return observed;
    }

    /// <summary>How many schema changes the cluster is already working on, or <c>null</c> if it would not say.</summary>
    private async static Task<int?> CountInFlightSchemaChangesAsync(
        DbConnection connection, ILogger logger, CancellationToken ct)
    {
        try
        {
            await using var command = connection.CreateCommand();

            command.CommandText =
                """
                WITH j AS (SHOW JOBS)
                SELECT count(*) FROM j
                 WHERE job_type IN ('SCHEMA CHANGE', 'TYPEDESC SCHEMA CHANGE', 'NEW SCHEMA CHANGE')
                   AND status IN ('pending', 'running', 'paused')
                """;

            return Convert.ToInt32(await command.ExecuteScalarAsync(ct));
        }
        catch (PostgresException e)
        {
            logger.LogWarning(e,
                "Could not read SHOW JOBS ({SqlState}); the reconciler cannot tell whether a schema change " +
                "is already running, so it issued nothing this pass", e.SqlState);

            return null;
        }
    }

    private async static Task ExecuteAsync(DbConnection connection, string sql, CancellationToken ct)
    {
        await using var command = connection.CreateCommand();

        command.CommandText = sql;

        await command.ExecuteNonQueryAsync(ct);
    }

    /// <summary>
    /// One line per pass, and one line per item that needs a human.
    /// </summary>
    /// <remarks>
    /// Quiet when there is nothing to say — the precedent is <c>ClusterClientStatus</c>, which logs a
    /// transition once "because it runs every few seconds and logging its verdict would turn a long
    /// outage into a long log saying the same thing". Loud, with the exact statement, when there is:
    /// a refusal nobody can see is indistinguishable from a reconciler that did not notice, and that is
    /// the difference between "the tool protected us" and "the tool was broken" at the incident review.
    /// </remarks>
    private static void Describe(SchemaTtlPlan plan, SchemaReconcileMode mode, SchemaChangeTier ceiling, ILogger logger)
    {
        if (plan.IsConverged)
        {
            logger.LogDebug("Schema TTL is converged across {Count} declared table(s)", plan.Items.Count);
            return;
        }

        logger.LogInformation(
            "Schema TTL reconcile in {Mode} up to tier {Ceiling}: {Drift} of {Count} declared table(s) differ",
            mode, ceiling, plan.Items.Count(item => item.Status is SchemaTtlStatus.Drift), plan.Items.Count);

        foreach (var item in plan.Items.Where(item => item.Status is not SchemaTtlStatus.Converged))
        {
            // A table that does not exist yet is not news: the CREATE TABLE that makes it already
            // carries the clause, which is the one path where the generator has always worked.
            if (item.Status is SchemaTtlStatus.Absent)
                logger.LogInformation("{Table}: {Reason}", item.Table, item.Reason);
            else if (item.Statement is null || item.Tier > ceiling || mode is not SchemaReconcileMode.Apply)
                logger.LogWarning("{Table} [{Status}/{Tier}] {Reason}{Sql}",
                    item.Table, item.Status, item.Tier, item.Reason,
                    item.Statement is null ? "" : $" — run: {item.Statement};");

            foreach (var note in item.Notes)
                logger.LogInformation("{Table}: {Note}", item.Table, note);
        }
    }
}
