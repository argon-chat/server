namespace Argon.Features.EF;

using Npgsql;
using System.Data.Common;

/// <summary>
/// Deletes the rows CockroachDB's row-level TTL would have deleted, on the engine that has no such
/// feature.
/// </summary>
/// <remarks>
/// <para><b>Why this exists at all.</b> <c>WithTTL</c> writes a <c>Job:Expiration</c> annotation that
/// <c>MultiregionalMigrationsSqlGenerator</c> turns into a <c>WITH (ttl = 'on', …)</c> clause — which
/// is CockroachDB syntax. On PostgreSQL the annotation is inert and there is nothing to emit, ever, so
/// the three tables that declare a TTL accumulate expired rows forever on every PostgreSQL deployment,
/// which is local development, the integration suite, and any single-region deployment. It is
/// accumulation and not a security hole: <c>InviteGrain</c> checks <c>ExpireAt</c> on both the accept
/// and the preview path, so an expired invite already answers <c>EXPIRED</c> whether the row is there
/// or not.</para>
///
/// <para><b>Why an application sweeper and not <c>pg_cron</c>.</b> <c>pg_cron</c> is an extension, and
/// its README requires all three of: an entry in <c>shared_preload_libraries</c>, which is a
/// postmaster-context setting and so needs a server restart; <c>CREATE EXTENSION</c> run as superuser;
/// and a metadata database, <c>postgres</c> by default, with cross-database jobs going through
/// <c>cron.schedule_in_database()</c>. None of that is available in the integration suite, which starts
/// a stock <c>postgres:17-alpine</c> container, or on a developer's laptop, and on a managed service it
/// is the provider's decision rather than Argon's. An extension is infrastructure; this is code.</para>
///
/// <para><b>Why not partitioning.</b> Dropping a partition is the cheapest possible delete, but it
/// requires the partition key in every primary key — all three of these tables would have to be
/// redesigned and re-migrated — and it expires rows by the bucket they were inserted into rather than
/// by the deadline stored in the row, which is not the same rule. For three small tables that is a
/// physical redesign to save a <c>DELETE</c> nobody can measure.</para>
///
/// <para><b>Level-triggered, and no transaction.</b> Every pass re-derives its targets from the model
/// and re-asks the server what matches; no cursor is stored and none is resumed, so a pass that dies
/// halfway costs nothing but the rows it had already deleted, which were rows it was going to delete.
/// Each batch is one auto-committed statement issued on a raw command rather than through EF, because
/// <c>AddPooledDatabase</c> configures <c>EnableRetryOnFailure</c> and a blind retry of a
/// <c>DELETE</c> whose outcome is unknown is the one thing a delete path must not do.</para>
/// </remarks>
public static class TtlSweeper
{
    /// <summary>
    /// The lease table this takes, which is deliberately not the reconciler's.
    /// </summary>
    /// <remarks>
    /// Two different resources: the reconciler serialises DDL against the schema, this serialises
    /// deletion of rows, and there is no reason one should ever block the other. Sharing
    /// <c>__SchemaReconcileLock</c> would have made an hourly sweep able to turn a pod's boot-time
    /// reconcile pass into <c>SkippedLock</c> — a verdict that means "somebody else is converging", so
    /// the pod would report knowing nothing about the schema because a delete job happened to be
    /// running. The mechanism is shared; the row is not.
    /// </remarks>
    public const string LockTable = "__TtlSweepLock";

    /// <summary>
    /// One pass. Counts in <see cref="TtlSweepMode.Report"/>, deletes in <see cref="TtlSweepMode.Apply"/>.
    /// </summary>
    public async static Task<TtlSweepReport> RunAsync(
        DbContext dbContext,
        DbConnection connection,
        TtlSweepOptions options,
        string roleId,
        ILogger logger,
        CancellationToken ct = default)
    {
        var started = Stopwatch.GetTimestamp();

        try
        {
            return await PassAsync(dbContext, connection, options, roleId, logger, ct);
        }
        finally
        {
            TtlSweepInstruments.Duration.Record(Stopwatch.GetElapsedTime(started).TotalMilliseconds);
        }
    }

    private async static Task<TtlSweepReport> PassAsync(
        DbContext dbContext,
        DbConnection connection,
        TtlSweepOptions options,
        string roleId,
        ILogger logger,
        CancellationToken ct)
    {
        if (options.Mode is TtlSweepMode.Off)
        {
            logger.LogInformation("The TTL sweeper is off ({Key}=Off); nothing was read", TtlSweepOptions.ModeKey);
            return TtlSweepReport.NotApplicable($"{TtlSweepOptions.ModeKey} is Off");
        }

        // Probed, not declared, and this is the guard the whole design hangs on. On CockroachDB the
        // database deletes these rows itself; a sweeper running there as well would be a second deleter
        // racing the first over the same predicate, on a cluster where the built-in job is already
        // rate-limited and range-aware and this is not. Database:Provider is not trusted for it because
        // an unset or misspelled key resolves to CockroachDb — which fails safe here by accident, and
        // the accident is not what should be relied on. DatabaseEngineProbe asks version().
        if (await DatabaseEngineProbe.DetectAsync(connection, ct) is DatabaseProviderKind.CockroachDb)
        {
            logger.LogInformation(
                "The server is CockroachDB, whose own row-level TTL job deletes these rows; the TTL " +
                "sweeper has nothing to do here and read nothing");

            return TtlSweepReport.NotApplicable(
                "the server is CockroachDB; its own row-level TTL deletes these rows");
        }

        var targets = TtlSweepTargets.Resolve(dbContext.Model, options);

        if (targets.Count == 0)
        {
            logger.LogInformation("The model declares no row-level TTL; nothing to sweep");
            return TtlSweepReport.From([], "the model declares no row-level TTL");
        }

        // Read-only, so no lease. Counting cannot corrupt anything and two pods counting at once is
        // two scans, not a race — taking a lock for it would only mean that whichever pod lost it
        // reported nothing, which is strictly less information for no safety.
        if (options.Mode is TtlSweepMode.Report)
        {
            var reported = new List<TtlSweepItem>(targets.Count);

            foreach (var target in targets)
                reported.Add(await CountAsync(connection, target, logger, ct));

            var report = TtlSweepReport.From(reported, "report only");

            Describe(report, options.Mode, logger);

            return report;
        }

        await using var lease = await SchemaReconcileLease.TryAcquireAsync(
            connection, logger, roleId, options.LeaseLifetime, LockTable, ct);

        // Exactly one sweeper across the fleet, and the grain's single activation is not enough on its
        // own to promise it: Orleans guarantees one activation per cluster, while the thing that must
        // not be swept twice is one database — which several clusters share the moment the regional
        // work lands. The lease is what makes the guarantee about the database rather than about the
        // ring.
        if (lease is null)
            return new TtlSweepReport(TtlSweepVerdict.SkippedLock,
                "another worker holds the TTL sweep lease", [], DateTimeOffset.UtcNow);

        var items = new List<TtlSweepItem>(targets.Count);
        var lost  = false;

        foreach (var target in targets)
        {
            if (!target.IsSweepable)
            {
                items.Add(TtlSweepItem.Refused(target));
                continue;
            }

            // Renewed before each table rather than only at the start. The lease has no background
            // heartbeat — see SchemaReconcileLease for why it cannot have one on a borrowed connection
            // — so this is the only place it can find out it was stolen, and continuing to delete under
            // a tenure that ended is the failure the fence exists to prevent.
            if (!await lease.TryRenewAsync(ct))
            {
                lost = true;
                break;
            }

            items.Add(await SweepAsync(connection, lease, target, logger, ct));
        }

        var pass = lost
            ? new TtlSweepReport(TtlSweepVerdict.SkippedLock,
                $"the TTL sweep lease was lost mid-pass after {items.Sum(item => item.Deleted)} row(s)",
                items, DateTimeOffset.UtcNow)
            : TtlSweepReport.From(items, $"as {lease.Holder} fence {lease.Fence}");

        Describe(pass, options.Mode, logger);

        return pass;
    }

    /// <summary>How many rows this target claims, capped, without touching one of them.</summary>
    private async static Task<TtlSweepItem> CountAsync(
        DbConnection connection, TtlSweepTarget target, ILogger logger, CancellationToken ct)
    {
        if (!target.IsSweepable)
            return TtlSweepItem.Refused(target);

        try
        {
            await using var command = connection.CreateCommand();

            command.CommandText = target.CountSql;

            var matched = Convert.ToInt64(await command.ExecuteScalarAsync(ct));
            var atLeast = matched > target.RowBudget;

            return matched == 0
                ? new TtlSweepItem(target.Table, TtlSweepStatus.Clean, "nothing has expired",
                    0, false, 0, 0, false, null)
                : new TtlSweepItem(target.Table, TtlSweepStatus.Reported,
                    $"{(atLeast ? "at least " : "")}{Math.Min(matched, target.RowBudget)} row(s) match " +
                    $"{target.Predicate} and nothing was deleted; set {TtlSweepOptions.ModeKey}=Apply to act on it",
                    Math.Min(matched, target.RowBudget), atLeast, 0, 0, false, target.DeleteBatchSql);
        }
        catch (PostgresException e)
        {
            logger.LogWarning(e, "Could not count expired rows in {Table} ({SqlState})", target.Table, e.SqlState);

            return new TtlSweepItem(target.Table, TtlSweepStatus.Failed,
                $"{e.SqlState} {e.MessageText}", null, false, 0, 0, false, null);
        }
    }

    /// <summary>
    /// Deletes one table's expired rows, a batch at a time, until there are none or the budget is spent.
    /// </summary>
    /// <remarks>
    /// <para>The loop stops on the first batch that removes nothing, which is the only honest
    /// termination condition: a batch can come back short of <c>LIMIT</c> because <c>SKIP LOCKED</c>
    /// stepped over rows another transaction is holding, and treating short as finished would leave
    /// those rows for a pass that never comes back for them.</para>
    ///
    /// <para>The pause happens between batches and not after the last one, so a table with a single
    /// batch of work costs a statement rather than a statement and a nap. The lease is renewed before
    /// each batch: the pause is the exact window in which a tenure can quietly end.</para>
    /// </remarks>
    private async static Task<TtlSweepItem> SweepAsync(
        DbConnection connection,
        SchemaReconcileLease lease,
        TtlSweepTarget target,
        ILogger logger,
        CancellationToken ct)
    {
        long deleted = 0;
        var  batches = 0;

        try
        {
            while (deleted < target.RowBudget)
            {
                if (batches > 0)
                {
                    await Task.Delay(target.BatchDelay, ct);

                    if (!await lease.TryRenewAsync(ct))
                        break;
                }

                await using var command = connection.CreateCommand();

                command.CommandText = target.DeleteBatchSql;

                var removed = await command.ExecuteNonQueryAsync(ct);

                batches++;

                if (removed <= 0)
                    break;

                deleted += removed;
            }
        }
        catch (PostgresException e)
        {
            // Never retried, and the pass continues to the next table rather than stopping. Each batch
            // auto-commits on its own, so what has been deleted is deleted and what has not will be
            // found again next pass; a failure here is nearly always a permission or a lock timeout on
            // one table, and letting it hide the other two would trade one visible problem for three
            // invisible ones.
            logger.LogError(e, "PostgreSQL refused a TTL sweep batch on {Table} ({SqlState}) after {Deleted} row(s)",
                target.Table, e.SqlState, deleted);

            return new TtlSweepItem(target.Table, TtlSweepStatus.Failed,
                $"{e.SqlState} {e.MessageText} (after {deleted} row(s))",
                null, false, deleted, batches, false, target.DeleteBatchSql);
        }

        var exhausted = deleted >= target.RowBudget;

        return deleted == 0
            ? new TtlSweepItem(target.Table, TtlSweepStatus.Clean, "nothing has expired", 0, false, 0, batches, false, null)
            : new TtlSweepItem(target.Table, TtlSweepStatus.Swept,
                $"deleted {deleted} row(s) matching {target.Predicate} in {batches} batch(es) of {target.BatchSize}" +
                (exhausted ? $"; the per-pass budget of {target.RowBudget} was reached, so more remain" : ""),
                null, false, deleted, batches, exhausted, target.DeleteBatchSql);
    }

    /// <summary>
    /// One line per pass, and one line per table that needs a human.
    /// </summary>
    /// <remarks>
    /// Quiet when there is nothing to say — the same rule the reconciler follows, and this one runs
    /// every hour rather than once per boot, so a pass that logged its verdict unconditionally would
    /// produce a log that is almost entirely itself. Loud, with the statement, for a refusal or a
    /// failure: those are the two states somebody has to act on.
    /// </remarks>
    private static void Describe(TtlSweepReport report, TtlSweepMode mode, ILogger logger)
    {
        if (report.Verdict is TtlSweepVerdict.Clean)
        {
            logger.LogDebug("TTL sweep found nothing expired across {Count} declared table(s)", report.Items.Count);
            return;
        }

        logger.LogInformation("TTL sweep in {Mode}: {Description}", mode, report.Description);

        foreach (var item in report.Items)
        {
            switch (item.Status)
            {
                case TtlSweepStatus.Refused:
                case TtlSweepStatus.Failed:
                    logger.LogWarning("{Table} [{Status}] {Reason}", item.Table, item.Status, item.Reason);
                    break;
                case TtlSweepStatus.Reported:
                case TtlSweepStatus.Swept:
                    logger.LogInformation("{Table} [{Status}] {Reason}", item.Table, item.Status, item.Reason);
                    break;
            }
        }
    }
}
