namespace Argon.Features.EF;

using System.Data.Common;
using System.Text.RegularExpressions;

/// <summary>
/// Exactly one process doing a piece of database maintenance at a time, and a way to find out it is no
/// longer the one.
/// </summary>
/// <remarks>
/// Written for the schema reconciler and named after it. It now carries three jobs: the reconciler, the
/// PostgreSQL TTL sweeper, and the boot path's migrations — each over a different row in a different
/// table. See the remark on <see cref="DefaultLockTable"/> for why the resource is a parameter and why
/// the three must not share. The name stayed because renaming a type that half a dozen files already
/// reason about is a larger diff than this sentence.
/// </remarks>
/// <remarks>
/// <para><b>Why this replaced the lock that used to be on the boot path.</b> <c>WarmUpExtension</c>
/// hand-rolled a lock over <c>__MigrationLock</c>, and the reconcile pass sits inside the migration
/// lock, so on a pod boot this lease is uncontended by construction. Reusing that lock was never an
/// option, because it had four properties that are survivable for "one pod applies migrations" and
/// wrong for anything that issues DDL against a live cluster: a fixed ten-minute TTL with no renewal,
/// so a long pass lets a second worker steal it and run concurrently; a release with no owner predicate
/// (<c>DELETE … WHERE id = 1</c>), so a holder whose lease was stolen deletes the <em>stealer's</em>
/// row on the way out and admits a third; an <c>expires_at</c> computed from the client's
/// <c>DateTime.UtcNow</c> and compared against the server's <c>now()</c>, so clock skew moves the TTL;
/// and a worker id of <c>Environment.MachineName</c>, which is not unique when several roles run as
/// processes on one host. This lease fixes all four, which is why warm-up now holds one of these over
/// <c>WarmUpExtension.MigrationLeaseTable</c> instead — a new row, because the deployed
/// <c>__MigrationLock</c> has no <c>fence</c> column and <c>CREATE TABLE IF NOT EXISTS</c> will not add
/// one to a table that is already there. It is also what the out-of-band entry point — the CLI or
/// Kubernetes Job that will run the tier the boot path refuses — holds when there is no migration lease
/// anywhere near it.</para>
///
/// <para><b>Why a lease and not something better.</b> There is nothing better available.
/// CockroachDB has no <c>LOCK TABLE</c> (which is why this repository ships
/// <c>NoLockHistoryRepository</c> at all), and it documents the advisory-lock functions as present with
/// <em>no-op implementations</em> — so <c>pg_advisory_lock</c> would lock nothing and say it had. An
/// Orleans grain cannot help either: warm-up runs before <c>RunAsync</c>, so no grain is activatable
/// yet, and moving the pass after it would mean serving traffic against an unreconciled schema. A row
/// in the database being reconciled is the only mechanism that works identically on both engines and
/// cannot be reachable while the thing it protects is not.</para>
///
/// <para><b>The renewal is explicit, and here is exactly what that does not cover.</b> A background
/// heartbeat needs a second connection — Npgsql will not run two commands on one — and this pass does
/// not have one: it borrows the connection warm-up already pinned. So the lease is renewed immediately
/// before each mutating statement instead, and the TTL is minutes rather than the seconds a heartbeat
/// would allow. The gap that leaves is a single statement blocking for longer than the whole TTL:
/// another worker could then steal the lease mid-statement. What makes that survivable rather than
/// silent is the fence — the next renewal fails, the runner stops instead of continuing under a lease
/// it no longer holds, and the statement it already issued was idempotent anyway. When the placement
/// workstream starts issuing <c>SET LOCALITY</c>, whose statements really do run for minutes, that is
/// the point at which a heartbeat on its own connection stops being optional.</para>
/// </remarks>
public sealed class SchemaReconcileLease : IAsyncDisposable
{
    /// <summary>The lease table the schema reconciler takes, and the default for callers that say nothing.</summary>
    public const string DefaultLockTable = "__SchemaReconcileLock";

    /// <summary>
    /// Three resources, three rows, one mechanism.
    /// </summary>
    /// <remarks>
    /// <para>The table name is a parameter rather than a constant because two other maintenance jobs —
    /// <see cref="TtlSweeper"/>, which deletes expired rows on PostgreSQL, and warm-up's migration
    /// pass — need the same four correctness properties this lease has and need them about
    /// <em>different</em> resources. One shared row would have made an hourly delete pass able to turn
    /// a pod's boot-time reconcile into <c>SkippedLock</c>, a verdict that claims another worker is
    /// converging the schema; sharing with migrations would have made a pod's own migration lock do it,
    /// since the reconcile pass runs a few lines later inside that same lock. Copying the class instead
    /// would have given each copy its own bugs, which is what the boot path used to have.</para>
    ///
    /// <para>It is validated rather than trusted: every caller is in this repository, but a lock table
    /// name is the one thing here that gets concatenated into DDL, and a class whose SQL is assembled
    /// from a string should say out loud what that string is allowed to be.</para>
    /// </remarks>
    private static string Delimit(string lockTable)
        => PlainIdentifier.IsMatch(lockTable)
            ? $"\"{lockTable}\""
            : throw new ArgumentException(
                $"'{lockTable}' is not a plain identifier and will not be used as a lease table name.",
                nameof(lockTable));

    private static readonly Regex PlainIdentifier = new(@"^[A-Za-z_][A-Za-z0-9_$]*$", RegexOptions.Compiled);

    /// <summary>
    /// Outside migration history, as the lock this replaced was, and with the same <c>TEXT</c> discipline.
    /// </summary>
    /// <remarks>
    /// <c>TEXT</c> rather than Cockroach's <c>STRING</c>, and no Cockroach-only syntax anywhere, because
    /// this DDL has to replay unchanged on vanilla PostgreSQL — the integration suite's default and
    /// local dev both run there. The reconciler itself no-ops on PostgreSQL, but the bootstrap must not
    /// be the thing that discovers that; the sweeper that shares this lease runs <em>only</em> on
    /// PostgreSQL, so it is now the common case rather than the awkward one.
    /// </remarks>
    private static string BootstrapSql(string table)
        => $"""
            CREATE TABLE IF NOT EXISTS {Delimit(table)} (
                id         INT PRIMARY KEY DEFAULT 1,
                fence      BIGINT      NOT NULL DEFAULT 0,
                locked_by  TEXT        NOT NULL,
                locked_at  TIMESTAMPTZ NOT NULL,
                expires_at TIMESTAMPTZ NOT NULL
            );
            """;

    /// <summary>
    /// Acquire or steal, in one compare-and-swap, with the server's clock on both sides of the
    /// comparison.
    /// </summary>
    /// <remarks>
    /// Both halves matter. One statement, because a read-then-write leaves a window two workers can
    /// both pass through. The server's <c>now()</c> on both sides, because the defect being avoided is
    /// a client clock deciding when somebody else's lease expired. The <c>WHERE</c> on the conflict
    /// branch is what makes a live lease unstealable; the <c>RETURNING</c> is empty when it fired.
    /// </remarks>
    private static string AcquireSql(string table)
        => $"""
            INSERT INTO {Delimit(table)} (id, fence, locked_by, locked_at, expires_at)
            VALUES (1, 1, @holder, now(), now() + @ttl::INTERVAL)
            ON CONFLICT (id) DO UPDATE
               SET fence      = {Delimit(table)}.fence + 1,
                   locked_by  = excluded.locked_by,
                   locked_at  = now(),
                   expires_at = now() + @ttl::INTERVAL
             WHERE {Delimit(table)}.expires_at < now()
            RETURNING fence;
            """;

    private static string RenewSql(string table)
        => $"""
            UPDATE {Delimit(table)}
               SET expires_at = now() + @ttl::INTERVAL
             WHERE id = 1 AND locked_by = @holder AND fence = @fence;
            """;

    private static string ReleaseSql(string table)
        => $"""
            DELETE FROM {Delimit(table)} WHERE id = 1 AND locked_by = @holder AND fence = @fence;
            """;

    /// <summary>
    /// Distinguishes two runs of the same role on the same host, which <c>MachineName</c> alone does not.
    /// </summary>
    /// <remarks>
    /// docker-compose and local dev run several roles as processes on one machine, so the machine name
    /// is shared; a pid is reused after a restart. The guid is minted once per process and never again,
    /// which is the property the owner predicate on release actually needs.
    /// </remarks>
    private static readonly Guid BootId = Guid.NewGuid();

    private readonly DbConnection connection;
    private readonly ILogger      logger;
    private readonly string       ttl;
    private readonly string       lockTable;

    private bool released;

    public string Holder { get; }

    /// <summary>Monotonic, incremented on every acquire. Identifies <em>this</em> tenure, not this holder.</summary>
    public long Fence { get; }

    private SchemaReconcileLease(
        DbConnection connection, ILogger logger, string holder, long fence, string ttl, string lockTable)
    {
        this.connection = connection;
        this.logger     = logger;
        this.ttl        = ttl;
        this.lockTable  = lockTable;

        Holder = holder;
        Fence  = fence;
    }

    /// <summary>The lease, or <c>null</c> when somebody else holds a live one.</summary>
    /// <remarks>
    /// Null is a correct outcome and the caller must treat it as one: another worker holds this
    /// resource, so this one has nothing to do and — crucially — has not established anything about the
    /// state of what the lease protects.
    /// </remarks>
    /// <param name="lockTable">
    /// Which resource is being taken. Defaults to the schema reconciler's, so its call site did not have
    /// to learn about this; <see cref="TtlSweeper.LockTable"/> and
    /// <c>WarmUpExtension.MigrationLeaseTable</c> are the other two.
    /// </param>
    public async static Task<SchemaReconcileLease?> TryAcquireAsync(
        DbConnection connection,
        ILogger logger,
        string roleId,
        TimeSpan lifetime,
        string lockTable = DefaultLockTable,
        CancellationToken ct = default)
    {
        await ExecuteAsync(connection, BootstrapSql(lockTable), ct);

        var holder = $"{Environment.MachineName}/{roleId}/{Environment.ProcessId}/{BootId:N}";
        var ttl    = $"{(long)lifetime.TotalSeconds} seconds";

        await using var command = connection.CreateCommand();

        command.CommandText = AcquireSql(lockTable);
        AddParameter(command, "holder", holder);
        AddParameter(command, "ttl", ttl);

        // No row comes back when the conflict branch's WHERE did not fire, which is the one and only
        // way this says "somebody else holds a live lease".
        if (await command.ExecuteScalarAsync(ct) is long fence)
        {
            logger.LogInformation("Lease on {LockTable} acquired by {Holder} at fence {Fence}", lockTable, holder, fence);
            return new SchemaReconcileLease(connection, logger, holder, fence, ttl, lockTable);
        }

        logger.LogInformation("Lease on {LockTable} is held by another worker; skipping this pass", lockTable);

        return null;
    }

    /// <summary>
    /// Pushes the expiry out, and reports whether this tenure still exists.
    /// </summary>
    /// <remarks>
    /// The return value is the point. Predicated on <see cref="Holder"/> <em>and</em>
    /// <see cref="Fence"/>, so a lease that expired and was taken by somebody else updates no rows and
    /// answers false — which is the runner's signal to stop rather than to keep issuing DDL under a
    /// tenure that ended. A renewal that merely logged would leave two workers converging at once,
    /// which CockroachDB turns into confusing errors on both.
    /// </remarks>
    public async Task<bool> TryRenewAsync(CancellationToken ct = default)
    {
        await using var command = connection.CreateCommand();

        command.CommandText = RenewSql(lockTable);
        AddParameter(command, "holder", Holder);
        AddParameter(command, "ttl", ttl);
        AddParameter(command, "fence", Fence);

        if (await command.ExecuteNonQueryAsync(ct) == 1)
            return true;

        logger.LogWarning(
            "Lease on {LockTable} at fence {Fence} is no longer held by {Holder}; stopping this pass",
            lockTable, Fence, Holder);

        return false;
    }

    /// <summary>
    /// Gives the lease up, and only if it is still ours.
    /// </summary>
    /// <remarks>
    /// The owner predicate is the fix for the defect <c>__MigrationLock</c> had: an unqualified delete
    /// by a holder whose lease was already stolen removes the <em>current</em> holder's row and lets a
    /// third worker in behind it. Failing to delete anything here is not an error — it means the lease
    /// had already moved on, which the runner will have found out from a renewal.
    /// </remarks>
    public async ValueTask DisposeAsync()
    {
        if (released)
            return;

        released = true;

        try
        {
            await using var command = connection.CreateCommand();

            command.CommandText = ReleaseSql(lockTable);
            AddParameter(command, "holder", Holder);
            AddParameter(command, "fence", Fence);

            await command.ExecuteNonQueryAsync();
        }
        catch (Exception e)
        {
            // Never allowed to escape: this runs on the boot path, and a process that refused to start
            // because it could not tidy up a lease that expires on its own would be trading a
            // self-healing condition for an outage. The sweeper's reminder tick has the same shape —
            // a failed release costs one skipped pass, and the lease expires on its own regardless.
            logger.LogWarning(e, "Could not release the lease on {LockTable} at fence {Fence}", lockTable, Fence);
        }
    }

    private async static Task ExecuteAsync(DbConnection connection, string sql, CancellationToken ct)
    {
        await using var command = connection.CreateCommand();

        command.CommandText = sql;

        await command.ExecuteNonQueryAsync(ct);
    }

    private static void AddParameter(DbCommand command, string name, object value)
    {
        var parameter = command.CreateParameter();

        parameter.ParameterName = name;
        parameter.Value         = value;

        command.Parameters.Add(parameter);
    }
}
