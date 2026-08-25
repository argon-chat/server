namespace ArgonComplexTest;

using Argon.Core.Features.EF;
using Argon.Entities;
using Argon.Features.EF;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;

/// <summary>
/// The lock the boot path takes, against a real engine.
/// </summary>
/// <remarks>
/// <para>Warm-up used to hand-roll its own lock over <c>__MigrationLock</c>. It had four defects and
/// one of them was load-bearing: the release was <c>DELETE FROM "__MigrationLock" WHERE id = 1</c>, no
/// owner predicate, in a <c>finally</c> — so a worker whose lease had expired and been stolen deleted
/// the <em>new</em> holder's row on the way out, and the next arrival then acquired an empty table
/// freely. Two workers applying migrations at once, on a path every pod of every role takes on every
/// boot. It now takes <see cref="SchemaReconcileLease"/>, the same lease the TTL sweeper holds over a
/// row of its own.</para>
///
/// <para>Two halves, and they prove different things. <c>#region the lease's promises</c> drives the
/// lease directly over a table of its own and asserts the three properties the boot path is now relying
/// on. <c>#region what the boot path did with it</c> asserts that the boot path actually took it —
/// those are the ones that go red against the code this replaced, because the table they look for did
/// not exist then.</para>
///
/// <para><b>Both engines.</b> Nothing here is skipped by provider: the lease is written to replay
/// unchanged on PostgreSQL and CockroachDB, and until now nothing exercised it on Cockroach at all
/// (the TTL sweeper that shares it refuses to run there by design). <c>ARGON_TEST_DB=Cockroach</c>
/// makes this the coverage for the engine production actually runs.</para>
///
/// <para><b>Non-parallelizable</b>, for the same reason <see cref="TtlSweepTests"/> is: the setup
/// creates and drops a table, which on CockroachDB is a schema-change job, and
/// <see cref="SchemaDeclarationTests"/> issues its own schema changes against the same database.
/// CockroachDB's own guidance is not to run more than one at a time, and two overlapping would make
/// either fixture red for a reason that has nothing to do with it.</para>
/// </remarks>
[TestFixture, NonParallelizable]
public class MigrationLeaseTests : TestBase
{
    /// <summary>
    /// A resource of this fixture's own, so nothing here touches the row a real boot depends on.
    /// </summary>
    /// <remarks>
    /// The tests below deliberately expire, steal and abandon leases. Doing that to
    /// <see cref="WarmUpExtension.MigrationLeaseTable"/> would be doing it to the row that decides
    /// whether the next host this suite boots is allowed to migrate — and <c>RoleStartupTests</c> boots
    /// several. A separate name costs one table and removes the coupling entirely.
    /// </remarks>
    private const string Table = "__MigrationLeaseTest";

    /// <summary>
    /// Zero, and it is not a placeholder.
    /// </summary>
    /// <remarks>
    /// The lease renders its lifetime as <c>'{n} seconds'::INTERVAL</c>, so a zero lifetime is a lease
    /// whose <c>expires_at</c> is the <c>now()</c> it was taken at — already expired to the next
    /// statement, which is exactly the state a stolen-from holder is in. Sleeping out a real TTL would
    /// prove the same thing and cost the suite the sleep.
    /// </remarks>
    private static readonly TimeSpan AlreadyExpired = TimeSpan.Zero;

    private static readonly TimeSpan LongEnough = TimeSpan.FromMinutes(5);

    /// <summary>
    /// A clean table, once, before anything in here runs.
    /// </summary>
    /// <remarks>
    /// Every test below gives its leases back, so this is not about tests interfering with each other —
    /// it is about <c>ARGON_TEST_REUSE_CONTAINERS</c>, which hands the next run the previous run's
    /// database. A live row left by a run that was cancelled halfway would make the first test here
    /// fail for a reason that happened yesterday.
    /// </remarks>
    [OneTimeSetUp]
    public async Task DropTheFixturesOwnLeaseTable()
    {
        await using var worker = await WorkerAsync(CancellationToken.None);

        await ExecuteAsync(worker, $"DROP TABLE IF EXISTS \"{Table}\"", CancellationToken.None);
    }

    /// <summary>
    /// One worker: its own context, its own pooled connection.
    /// </summary>
    /// <remarks>
    /// The connection is opened here and handed to the lease, which is how warm-up does it — the lease
    /// and the statements it protects have to share one session, or the pool can give the session away
    /// mid-tenure and the lease protects a connection nobody is migrating on.
    /// </remarks>
    private async Task<ApplicationDbContext> WorkerAsync(CancellationToken ct)
    {
        var db = await FactoryAsp.Services
           .GetRequiredService<IDbContextFactory<ApplicationDbContext>>()
           .CreateDbContextAsync(ct);

        await db.Database.OpenConnectionAsync(ct);

        return db;
    }

    /// <summary>
    /// A lease attempt for one worker, under a role id of its own.
    /// </summary>
    /// <remarks>
    /// The role id matters here and is not decoration. A holder is
    /// <c>{machine}/{role}/{pid}/{boot-guid}</c>, and every worker in this fixture shares a machine, a
    /// process and a boot guid — so without distinct role ids the two "workers" would be
    /// indistinguishable and the owner predicate would be tested by nothing. Distinct roles on one host
    /// is also the real case the holder id was designed for: docker-compose and local dev run several.
    /// </remarks>
    private Task<SchemaReconcileLease?> AcquireAsync(
        ApplicationDbContext worker, string roleId, TimeSpan lifetime, CancellationToken ct)
        => SchemaReconcileLease.TryAcquireAsync(
            worker.Database.GetDbConnection(),
            FactoryAsp.Services.GetRequiredService<ILoggerFactory>().CreateLogger("migration-lease-test"),
            roleId,
            lifetime,
            Table,
            ct);

    private async static Task ExecuteAsync(ApplicationDbContext worker, string sql, CancellationToken ct)
    {
        await using var command = worker.Database.GetDbConnection().CreateCommand();

        command.CommandText = sql;

        await command.ExecuteNonQueryAsync(ct);
    }

    private async static Task<long> ScalarAsync(
        ApplicationDbContext worker, string sql, CancellationToken ct, params (string Name, object Value)[] parameters)
    {
        await using var command = worker.Database.GetDbConnection().CreateCommand();

        command.CommandText = sql;

        foreach (var (name, value) in parameters)
        {
            var parameter = command.CreateParameter();

            parameter.ParameterName = name;
            parameter.Value         = value;

            command.Parameters.Add(parameter);
        }

        return Convert.ToInt64(await command.ExecuteScalarAsync(ct));
    }

    /// <summary>How many rows in the fixture's lease table belong to exactly this tenure.</summary>
    private static Task<long> RowsHeldByAsync(
        ApplicationDbContext worker, SchemaReconcileLease lease, CancellationToken ct)
        => ScalarAsync(worker,
            $"SELECT count(*) FROM \"{Table}\" WHERE id = 1 AND locked_by = @holder AND fence = @fence",
            ct, ("holder", lease.Holder), ("fence", lease.Fence));

    #region the lease's promises

    /// <summary>
    /// A second worker is refused while the first holds a live lease.
    /// </summary>
    /// <remarks>
    /// The base case, and the one that has to hold before any of the rest means anything. The refusal
    /// is a <c>null</c> rather than an exception because it is a correct outcome: somebody else is
    /// migrating, so this pod skips and boots. What it must never be is a lease.
    /// </remarks>
    [Test, CancelAfter(120_000)]
    public async Task A_live_lease_cannot_be_taken_by_a_second_worker(CancellationToken ct = default)
    {
        await using var first  = await WorkerAsync(ct);
        await using var second = await WorkerAsync(ct);

        await using var held = await AcquireAsync(first, "worker-a", LongEnough, ct);

        Assert.That(held, Is.Not.Null, "the first worker should have taken an untaken lease");

        // `await using` on something asserted to be null looks odd and is not: if the assertion fails
        // there is a real lease here, and leaving it to expire would fail the next test in this fixture
        // for a reason belonging to this one.
        await using var refused = await AcquireAsync(second, "worker-b", LongEnough, ct);

        Assert.That(refused, Is.Null,
            $"a second worker took {Table} while {held!.Holder} still holds it at fence {held.Fence}");
    }

    /// <summary>
    /// An expired lease can be stolen, and the fence moves when it is.
    /// </summary>
    /// <remarks>
    /// <para>Both halves are the point. Stealable, because a pod that died holding the lease must not
    /// stop the fleet migrating forever. And the fence moving, because that is the only thing that
    /// tells the two tenures apart afterwards — the old holder is still alive, still believes it holds
    /// something, and is about to run a <c>finally</c>.</para>
    ///
    /// <para>The expiry is decided by the server on both sides: <c>expires_at</c> was written as
    /// <c>now() + interval</c> and the steal predicate compares it to <c>now()</c>. That is the fix for
    /// the defect where a pod's own <c>DateTime.UtcNow</c> decided when somebody else's lock had run
    /// out, and it is why this test needs no clock of its own.</para>
    /// </remarks>
    [Test, CancelAfter(120_000)]
    public async Task An_expired_lease_can_be_stolen_and_the_fence_moves(CancellationToken ct = default)
    {
        await using var first  = await WorkerAsync(ct);
        await using var second = await WorkerAsync(ct);

        await using var stale = await AcquireAsync(first, "worker-a", AlreadyExpired, ct);

        Assert.That(stale, Is.Not.Null);

        await using var stolen = await AcquireAsync(second, "worker-b", LongEnough, ct);

        Assert.That(stolen, Is.Not.Null,
            "an expired lease must be stealable, or one dead pod stops the whole fleet migrating");

        Assert.That(stolen!.Fence, Is.GreaterThan(stale!.Fence),
            $"{stolen.Holder} took the lease at fence {stolen.Fence}, which is not past the "
          + $"{stale.Fence} that {stale.Holder} still believes it holds — without a fence that moves, "
          + "a stolen-from holder has no way to find out its tenure ended");
    }

    /// <summary>
    /// A stolen-from holder's release does not delete the new holder's row.
    /// </summary>
    /// <remarks>
    /// <para><b>What this does and does not guard.</b> It is a property test of the lease, not a
    /// regression test against the old lock — the lease's release has carried these predicates since it
    /// was written, so this assertion was true before the boot path moved onto it and is true after.
    /// What actually distinguishes the two builds is that the boot path no longer implements a lock at
    /// all: <c>The_boot_path_carries_no_lock_of_its_own</c> in the fast suite, and
    /// <c>Warm_up_bootstrapped_a_fenced_lease_table</c> / <c>Warm_up_gave_the_lease_back</c> here. Do
    /// not read a green here as evidence the boot path is correct; read it as the reason it is safe to
    /// delegate to the lease.</para>
    ///
    /// <para>The defect it stands against is a future one — somebody dropping the predicates while
    /// tidying. That is worth pinning, because the old release was
    /// <c>DELETE FROM "__MigrationLock" WHERE id = 1</c>, unconditional, running in a
    /// <c>finally</c>. Worker A's lease expires; worker B steals it and starts migrating; worker A
    /// reaches its <c>finally</c> and deletes B's row; worker C arrives, finds an empty table, and
    /// acquires. A and C and B are now all inside the lock. Nothing logs anything.</para>
    ///
    /// <para>So this asserts both halves of the failure and not just the tidy one. That B's row
    /// survives is the mechanism; that C is still refused afterwards is the consequence, and it is the
    /// assertion that would have caught the original defect — a release that leaves the row behind but
    /// somehow still admits a third worker would be just as broken and would pass a row count.</para>
    ///
    /// <para>The fence is what carries this, not the holder: two workers in one test process share a
    /// machine, a pid and a boot guid, so their holder ids differ only by the role id this fixture
    /// gives them. In production they differ by all four. Either way the predicate is
    /// <c>locked_by AND fence</c>, and it is the fence that makes two tenures of the <em>same</em>
    /// holder distinguishable.</para>
    /// </remarks>
    [Test, CancelAfter(120_000)]
    public async Task A_stolen_from_holder_releases_nothing_and_admits_nobody(CancellationToken ct = default)
    {
        await using var first  = await WorkerAsync(ct);
        await using var second = await WorkerAsync(ct);
        await using var third  = await WorkerAsync(ct);

        // Not `await using`: this one has to be released in the middle of the test rather than at the
        // end of it, because the whole scenario is what its release does to somebody else's row.
        var stale = await AcquireAsync(first, "worker-a", AlreadyExpired, ct);

        Assert.That(stale, Is.Not.Null);

        await using var holder = await AcquireAsync(second, "worker-b", LongEnough, ct);

        Assert.That(holder, Is.Not.Null, "the expired lease should have been stolen");

        // Worker A's `finally`, in other words — a holder that does not know its tenure ended.
        await stale!.DisposeAsync();

        var             survived = await RowsHeldByAsync(second, holder!, ct);
        await using var admitted = await AcquireAsync(third, "worker-c", LongEnough, ct);

        // Read out before the assertion block rather than captured into it: the leases are disposed at
        // the end of this method, and a lambda that reaches into one is a lambda that outlives it on
        // paper even when it does not in practice.
        var loser  = $"{stale.Holder} at fence {stale.Fence}";
        var winner = $"{holder!.Holder} at fence {holder.Fence}";

        Assert.Multiple(() =>
        {
            Assert.That(survived, Is.EqualTo(1L),
                $"{loser} deleted the row belonging to {winner} — that is the unconditional-delete defect");
            Assert.That(admitted, Is.Null,
                "a third worker got the lease while the second still held it, which is what an "
              + "unqualified release lets happen");
        });
    }

    #endregion

    #region what the boot path did with it

    /// <summary>
    /// Warm-up bootstrapped a lease table with a fence in it.
    /// </summary>
    /// <remarks>
    /// <para>The tests above prove the lease is correct; this proves the boot path is the thing holding
    /// it. It is also the assertion that settles the migration question, because the shape is the whole
    /// argument: the deployed <c>__MigrationLock</c> has <c>(id, locked_at, locked_by, expires_at)</c>
    /// and no <c>fence</c>, and <c>CREATE TABLE IF NOT EXISTS</c> adds no column to a table that
    /// already exists — so a lease pointed at the old name would have five columns in its
    /// <c>INSERT</c> and four in the table, on the boot path, of every pod. A new table is what makes
    /// the bootstrap a no-op on the second boot instead of a schema migration on every one.</para>
    ///
    /// <para><c>__MigrationLock</c> is deliberately not asserted absent. It is abandoned, not dropped:
    /// nothing writes to it any more and an operator can drop it whenever it suits them, which is a
    /// smaller thing to get wrong than an <c>ALTER</c> issued concurrently by a booting fleet.</para>
    /// </remarks>
    [Test, CancelAfter(120_000)]
    public async Task Warm_up_bootstrapped_a_fenced_lease_table(CancellationToken ct = default)
    {
        await using var worker = await WorkerAsync(ct);

        // The table name goes in as a literal rather than a parameter, as TtlSweepTests does for the
        // same catalog read: information_schema.columns.table_name is a domain over `name`, and how a
        // driver-inferred text parameter compares against that is a question the two engines do not
        // have to answer the same way. The value is a compile-time constant the fast suite pins to a
        // plain identifier, so there is nothing here for a quote to get into.
        //
        // Counted by name rather than as `count(*)` over the whole table, because a bare count would be
        // an assertion about how many columns each engine chooses to expose rather than about which
        // ones this lease needs.
        var columns = await ScalarAsync(worker,
            "SELECT count(*) FROM information_schema.columns "
          + $"WHERE table_name = '{WarmUpExtension.MigrationLeaseTable}' "
          + "AND column_name IN ('id', 'fence', 'locked_by', 'locked_at', 'expires_at')", ct);

        var fence = await ScalarAsync(worker,
            "SELECT count(*) FROM information_schema.columns "
          + $"WHERE table_name = '{WarmUpExtension.MigrationLeaseTable}' AND column_name = 'fence'", ct);

        Assert.Multiple(() =>
        {
            Assert.That(columns, Is.EqualTo(5L),
                $"warm-up should have bootstrapped {WarmUpExtension.MigrationLeaseTable} with "
              + "(id, fence, locked_by, locked_at, expires_at); a zero here means the boot path is not "
              + "taking this lease at all");
            Assert.That(fence, Is.EqualTo(1L),
                "a lease table with no fence cannot tell two tenures apart, which is exactly what the "
              + "old __MigrationLock could not do");
        });
    }

    /// <summary>
    /// And gave the lease back when it had finished.
    /// </summary>
    /// <remarks>
    /// <para>The release is predicated now, which raises a question the old unconditional delete never
    /// had to answer: does it still fire on the happy path? A predicate that never matches would leave
    /// the row behind, and because the row is the lock, every pod booting for the next ten minutes
    /// would skip migrating and publish <c>SkippedLock</c> — a fleet that quietly stops applying
    /// migrations rather than one that fails.</para>
    ///
    /// <para>Safe to assert because this fixture is <c>NonParallelizable</c>: no host is part-way
    /// through warm-up while it runs, so a live row here is a leak and not a race.</para>
    /// </remarks>
    [Test, CancelAfter(120_000)]
    public async Task Warm_up_gave_the_lease_back(CancellationToken ct = default)
    {
        await using var worker = await WorkerAsync(ct);

        var held = await ScalarAsync(worker,
            $"SELECT count(*) FROM \"{WarmUpExtension.MigrationLeaseTable}\"", ct);

        Assert.That(held, Is.Zero,
            $"{WarmUpExtension.MigrationLeaseTable} still holds a row after warm-up finished; every pod "
          + "that boots before it expires will skip migrating");
    }

    #endregion
}
