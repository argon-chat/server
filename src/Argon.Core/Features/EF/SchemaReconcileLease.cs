namespace Argon.Features.EF;

using System.Data.Common;

/// <summary>
/// Exactly one process converging the schema at a time, and a way to find out it is no longer the one.
/// </summary>
/// <remarks>
/// <para><b>Why not the lock that already exists.</b> <c>WarmUpExtension</c>'s <c>__MigrationLock</c>
/// serialises the boot path and the reconcile pass sits inside it, so on a pod boot this lease is
/// uncontended by construction. It is not redundant, because <c>__MigrationLock</c> has four properties
/// that are fine for "one pod applies migrations" and wrong for a thing that issues DDL against a live
/// cluster: a fixed ten-minute TTL with no renewal, so a long pass lets a second worker steal it and
/// run concurrently; a release with no owner predicate (<c>DELETE … WHERE id = 1</c>), so a holder
/// whose lease was stolen deletes the <em>stealer's</em> row on the way out and admits a third; an
/// <c>expires_at</c> computed from the client's <c>DateTime.UtcNow</c> and compared against the
/// server's <c>now()</c>, so clock skew moves the TTL; and a worker id of <c>Environment.MachineName</c>,
/// which is not unique when several roles run as processes on one host. This lease fixes all four, and
/// it is what the out-of-band entry point — the CLI or Kubernetes Job that will run the tier the boot
/// path refuses — holds when there is no migration lock anywhere near it.</para>
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
    /// <summary>
    /// Outside migration history, like <c>__MigrationLock</c>, and with the same <c>TEXT</c> discipline.
    /// </summary>
    /// <remarks>
    /// <c>TEXT</c> rather than Cockroach's <c>STRING</c>, and no Cockroach-only syntax anywhere, because
    /// this DDL has to replay unchanged on vanilla PostgreSQL — the integration suite's default and
    /// local dev both run there. The reconciler itself no-ops on PostgreSQL, but the bootstrap must not
    /// be the thing that discovers that.
    /// </remarks>
    private const string BootstrapSql =
        """
        CREATE TABLE IF NOT EXISTS "__SchemaReconcileLock" (
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
    private const string AcquireSql =
        """
        INSERT INTO "__SchemaReconcileLock" (id, fence, locked_by, locked_at, expires_at)
        VALUES (1, 1, @holder, now(), now() + @ttl::INTERVAL)
        ON CONFLICT (id) DO UPDATE
           SET fence      = "__SchemaReconcileLock".fence + 1,
               locked_by  = excluded.locked_by,
               locked_at  = now(),
               expires_at = now() + @ttl::INTERVAL
         WHERE "__SchemaReconcileLock".expires_at < now()
        RETURNING fence;
        """;

    private const string RenewSql =
        """
        UPDATE "__SchemaReconcileLock"
           SET expires_at = now() + @ttl::INTERVAL
         WHERE id = 1 AND locked_by = @holder AND fence = @fence;
        """;

    private const string ReleaseSql =
        """
        DELETE FROM "__SchemaReconcileLock" WHERE id = 1 AND locked_by = @holder AND fence = @fence;
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

    private bool released;

    public string Holder { get; }

    /// <summary>Monotonic, incremented on every acquire. Identifies <em>this</em> tenure, not this holder.</summary>
    public long Fence { get; }

    private SchemaReconcileLease(DbConnection connection, ILogger logger, string holder, long fence, string ttl)
    {
        this.connection = connection;
        this.logger     = logger;
        this.ttl        = ttl;

        Holder = holder;
        Fence  = fence;
    }

    /// <summary>The lease, or <c>null</c> when somebody else holds a live one.</summary>
    /// <remarks>
    /// Null is a correct outcome and the caller must treat it as one: another worker is reconciling, so
    /// this one has nothing to do and — crucially — has not established that anything is converged.
    /// </remarks>
    public async static Task<SchemaReconcileLease?> TryAcquireAsync(
        DbConnection connection, ILogger logger, string roleId, TimeSpan lifetime, CancellationToken ct = default)
    {
        await ExecuteAsync(connection, BootstrapSql, ct);

        var holder = $"{Environment.MachineName}/{roleId}/{Environment.ProcessId}/{BootId:N}";
        var ttl    = $"{(long)lifetime.TotalSeconds} seconds";

        await using var command = connection.CreateCommand();

        command.CommandText = AcquireSql;
        AddParameter(command, "holder", holder);
        AddParameter(command, "ttl", ttl);

        // No row comes back when the conflict branch's WHERE did not fire, which is the one and only
        // way this says "somebody else holds a live lease".
        if (await command.ExecuteScalarAsync(ct) is long fence)
        {
            logger.LogInformation("Schema reconcile lease acquired by {Holder} at fence {Fence}", holder, fence);
            return new SchemaReconcileLease(connection, logger, holder, fence, ttl);
        }

        logger.LogInformation("Schema reconcile lease is held by another worker; skipping this pass");

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

        command.CommandText = RenewSql;
        AddParameter(command, "holder", Holder);
        AddParameter(command, "ttl", ttl);
        AddParameter(command, "fence", Fence);

        if (await command.ExecuteNonQueryAsync(ct) == 1)
            return true;

        logger.LogWarning(
            "Schema reconcile lease at fence {Fence} is no longer held by {Holder}; stopping this pass",
            Fence, Holder);

        return false;
    }

    /// <summary>
    /// Gives the lease up, and only if it is still ours.
    /// </summary>
    /// <remarks>
    /// The owner predicate is the fix for the defect <c>__MigrationLock</c> has: an unqualified delete
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

            command.CommandText = ReleaseSql;
            AddParameter(command, "holder", Holder);
            AddParameter(command, "fence", Fence);

            await command.ExecuteNonQueryAsync();
        }
        catch (Exception e)
        {
            // Never allowed to escape: this runs on the boot path, and a process that refused to start
            // because it could not tidy up a lease that expires on its own would be trading a
            // self-healing condition for an outage.
            logger.LogWarning(e, "Could not release the schema reconcile lease at fence {Fence}", Fence);
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
