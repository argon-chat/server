namespace ArgonComplexTest;

using Argon.Entities;
using Argon.Features.EF;
using Argon.Grains.Interfaces;
using Infrastructure;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;

/// <summary>
/// The sweeper against a server, rather than against a model.
/// </summary>
/// <remarks>
/// <para>The fast suite proves the predicate selects the right rows and the batching is paced. This
/// proves the statements are ones PostgreSQL accepts — <c>ctid IN (… FOR UPDATE SKIP LOCKED)</c> is
/// exactly the kind of clause that is either correct or a syntax error and never anything in between —
/// and that after a pass the row is actually gone and its neighbour is not. Those are not things a unit
/// test can see.</para>
///
/// <para><b>Deleting is exercised here, and only here.</b> Every other suite runs the sweeper in
/// <see cref="TtlSweepMode.Report"/>, which is also the default the server boots with; the apply pass
/// below opts in explicitly, per call, against rows this fixture seeded itself. The fixture is
/// <c>NonParallelizable</c> for a concrete reason and not out of caution:
/// <c>SpaceTests.PreviewInvite_WithExpiredCode_ReturnsExpired</c> seeds an expired invite and then
/// asserts the preview says <c>EXPIRED</c>, and an apply pass overlapping it would delete that row and
/// turn the answer into <c>NOT_FOUND</c>.</para>
/// </remarks>
[TestFixture, NonParallelizable]
public class TtlSweepTests : TestBase
{
    private static void OnlyOnPostgres()
        => Assume.That(TestEnvironmentOptions.DatabaseKind, Is.EqualTo(TestDatabaseKind.Postgres),
            "the sweeper exists because PostgreSQL has no row-level TTL; on CockroachDB it refuses by design");

    private static readonly TableRef Invites        = new("public", "Invites");
    private static readonly TableRef FriendRequests = new("public", "user_friend_requests");

    /// <summary>One pass against the running server, in the mode the case is about.</summary>
    /// <remarks>
    /// The connection is opened on the context and handed to the sweeper, which is how the grain does
    /// it: the lease and the delete batches have to share one session or the lease protects nothing.
    /// </remarks>
    private async Task<TtlSweepReport> SweepAsync(TtlSweepMode mode, CancellationToken ct)
    {
        await using var db = await FactoryAsp.Services
           .GetRequiredService<IDbContextFactory<ApplicationDbContext>>()
           .CreateDbContextAsync(ct);

        await db.Database.OpenConnectionAsync(ct);

        return await TtlSweeper.RunAsync(
            db,
            db.Database.GetDbConnection(),
            TtlSweepOptions.Default with { Mode = mode, MinimumBatchDelay = TimeSpan.Zero },
            roleId: nameof(TtlSweepTests),
            FactoryAsp.Services.GetRequiredService<ILoggerFactory>().CreateLogger("ttl-sweep-test"),
            ct);
    }

    private static string Explain(TtlSweepReport report)
        => $"{report.Verdict}: {report.Description}\n" + string.Join("\n", report.Items.Select(item =>
            $"{item.Table} [{item.Status}] matched={item.Matched} deleted={item.Deleted} — {item.Reason}"));

    private static TtlSweepItem Item(TtlSweepReport report, TableRef table)
        => report.Items.SingleOrDefault(item => item.Table == table)
           ?? throw new AssertionException($"{table} is missing from the pass:\n{Explain(report)}");

    /// <summary>
    /// A space with one invite already expired and one still live.
    /// </summary>
    /// <remarks>
    /// Seeded straight into the table because the API refuses to mint an invite in the past — the same
    /// reason <c>SpaceTests</c> reaches for the context. The live one is the control: a sweep that
    /// deletes by the right predicate takes exactly one of these two, and a sweep that deletes by a
    /// wrong one takes both without anybody noticing.
    /// </remarks>
    private async Task<(ulong Expired, ulong Live)> SeedAsync(CancellationToken ct)
    {
        var token = await RegisterAndGetTokenAsync(ct);
        SetAuthToken(token);

        await using var scope = FactoryAsp.Services.CreateAsyncScope();

        var me      = await GetUserService(scope.ServiceProvider).GetMe(ct);
        var spaceId = await CreateSpaceAndGetIdAsync(ct);

        await using var db = await FactoryAsp.Services
           .GetRequiredService<IDbContextFactory<ApplicationDbContext>>()
           .CreateDbContextAsync(ct);

        var expired = Add(db, spaceId, me.userId, DateTimeOffset.UtcNow.AddHours(-1));
        var live    = Add(db, spaceId, me.userId, DateTimeOffset.UtcNow.AddDays(30));

        await db.SaveChangesAsync(ct);

        return (expired, live);
    }

    private static ulong Add(ApplicationDbContext db, Guid spaceId, Guid creatorId, DateTimeOffset expireAt)
    {
        var id = InviteCodeEntityData.EncodeToUlong(InviteCodeEntityData.GenerateInviteCode());

        db.Invites.Add(new SpaceInvite
        {
            Id        = id,
            SpaceId   = spaceId,
            CreatorId = creatorId,
            ExpireAt  = expireAt,
            CreatedAt = DateTimeOffset.UtcNow,
            UpdatedAt = DateTimeOffset.UtcNow
        });

        return id;
    }

    private async Task<bool> ExistsAsync(ulong id, CancellationToken ct)
    {
        await using var db = await FactoryAsp.Services
           .GetRequiredService<IDbContextFactory<ApplicationDbContext>>()
           .CreateDbContextAsync(ct);

        return await db.Invites.AsNoTracking().AnyAsync(invite => invite.Id == id, ct);
    }

    /// <summary>
    /// The default mode counts and changes nothing, which is the promise the whole design rests on.
    /// </summary>
    /// <remarks>
    /// If this ever goes red, the failure is not that a number was wrong — it is that a mode documented
    /// as a dry run deleted a row. Both invites are checked afterwards, including the expired one that
    /// a correct apply pass <em>would</em> take.
    /// </remarks>
    [Test, CancelAfter(120_000)]
    public async Task A_report_pass_counts_the_expired_row_and_deletes_nothing(CancellationToken ct = default)
    {
        OnlyOnPostgres();

        var (expired, live) = await SeedAsync(ct);

        var report = await SweepAsync(TtlSweepMode.Report, ct);
        var item   = Item(report, Invites);

        // Both reads happen before the assertion block: an await inside Assert.Multiple runs as
        // async void, which swallows the failure and reports the wrong test.
        var expiredSurvived = await ExistsAsync(expired, ct);
        var liveSurvived    = await ExistsAsync(live, ct);

        Assert.Multiple(() =>
        {
            Assert.That(item.Status, Is.EqualTo(TtlSweepStatus.Reported), Explain(report));
            Assert.That(item.Matched ?? 0, Is.GreaterThanOrEqualTo(1L), Explain(report));
            Assert.That(item.Deleted, Is.Zero, "report mode must never delete a row");
            Assert.That(report.Deleted, Is.Zero, Explain(report));
            Assert.That(expiredSurvived, Is.True, "the expired invite must survive a report pass");
            Assert.That(liveSurvived, Is.True);
        });
    }

    /// <summary>
    /// An apply pass takes the expired invite and leaves the live one.
    /// </summary>
    /// <remarks>
    /// <para>The acceptance criterion, and the only test in the repository that lets this code delete
    /// anything. The live invite is what makes it meaningful: <c>"ExpireAt" &lt; now()</c> and
    /// <c>TRUE</c> both pass a test that only checks the expired row is gone.</para>
    ///
    /// <para>It also proves the statement itself is legal PostgreSQL. <c>DELETE … WHERE ctid IN (SELECT
    /// ctid … LIMIT n FOR UPDATE SKIP LOCKED)</c> is assembled from strings and never compiled by
    /// anything, so a stray clause is a runtime <c>42601</c> — which the report would faithfully record
    /// as a failure while the rows quietly stayed put.</para>
    /// </remarks>
    [Test, CancelAfter(120_000)]
    public async Task An_apply_pass_deletes_the_expired_invite_and_spares_the_live_one(CancellationToken ct = default)
    {
        OnlyOnPostgres();

        var (expired, live) = await SeedAsync(ct);

        var report = await SweepAsync(TtlSweepMode.Apply, ct);
        var item   = Item(report, Invites);

        var expiredSurvived = await ExistsAsync(expired, ct);
        var liveSurvived    = await ExistsAsync(live, ct);

        Assert.Multiple(() =>
        {
            Assert.That(item.Status, Is.EqualTo(TtlSweepStatus.Swept), Explain(report));
            Assert.That(item.Deleted, Is.GreaterThanOrEqualTo(1L), Explain(report));
            Assert.That(expiredSurvived, Is.False, "the expired invite should be gone");
            Assert.That(liveSurvived, Is.True, "a live invite must never be swept");
        });
    }

    /// <summary>
    /// A second pass over the same table finds nothing left to do.
    /// </summary>
    /// <remarks>
    /// The property that makes an hourly job safe. A sweep that kept finding the rows it had already
    /// deleted would mean the predicate and the delete disagree about what they select — the shape of
    /// bug that presents as constant activity rather than as an error.
    /// </remarks>
    [Test, CancelAfter(120_000)]
    public async Task A_second_pass_finds_the_row_already_gone(CancellationToken ct = default)
    {
        OnlyOnPostgres();

        var (expired, _) = await SeedAsync(ct);

        await SweepAsync(TtlSweepMode.Apply, ct);

        var second = await SweepAsync(TtlSweepMode.Apply, ct);

        var survived = await ExistsAsync(expired, ct);

        Assert.Multiple(() =>
        {
            Assert.That(survived, Is.False);
            Assert.That(Item(second, Invites).Deleted, Is.Zero, Explain(second));
        });
    }

    /// <summary>
    /// An apply pass over <c>user_friend_requests</c> takes nothing, because nothing is due.
    /// </summary>
    /// <remarks>
    /// <para>The assertion looks weak and is not. This table declared its TTL against
    /// <c>RequestedAt</c> — a column defaulted to <c>now()</c> on insert — which made the predicate
    /// true for every row that had ever been written. An apply pass would have emptied the table. The
    /// declaration now names <c>ExpiredAt</c>, the deadline <c>FriendsGrain</c> stores six months out,
    /// and <c>Deleted == 0</c> against a database holding real rows is exactly the evidence that the
    /// predicate no longer selects all of them.</para>
    ///
    /// <para>Mode is <c>Apply</c> on purpose. A report pass would prove nothing here: it deletes
    /// nothing whatever the predicate says, so it cannot tell a repaired declaration from the broken
    /// one. The whole point is that the pass is allowed to delete and chooses not to.</para>
    /// </remarks>
    [Test, CancelAfter(120_000)]
    public async Task An_unexpired_friend_request_survives_an_apply_pass(CancellationToken ct = default)
    {
        OnlyOnPostgres();

        var report = await SweepAsync(TtlSweepMode.Apply, ct);
        var item   = Item(report, FriendRequests);

        Assert.Multiple(() =>
        {
            Assert.That(item.Status, Is.Not.EqualTo(TtlSweepStatus.Refused), Explain(report));
            Assert.That(item.Deleted, Is.Zero, Explain(report));
        });
    }

    /// <summary>
    /// The sweep takes a lease, and not the reconciler's.
    /// </summary>
    /// <remarks>
    /// Two rows for two resources. Sharing <c>__SchemaReconcileLock</c> would have made an hourly delete
    /// pass able to turn a pod's boot-time reconcile into <c>SkippedLock</c> — a verdict that means
    /// "another worker is converging the schema", reported because something was deleting rows.
    /// </remarks>
    [Test, CancelAfter(120_000)]
    public async Task An_apply_pass_takes_its_own_lease_table(CancellationToken ct = default)
    {
        OnlyOnPostgres();

        await SweepAsync(TtlSweepMode.Apply, ct);

        await using var db = await FactoryAsp.Services
           .GetRequiredService<IDbContextFactory<ApplicationDbContext>>()
           .CreateDbContextAsync(ct);

        await db.Database.OpenConnectionAsync(ct);

        await using var command = db.Database.GetDbConnection().CreateCommand();

        command.CommandText =
            "SELECT count(*) FROM information_schema.tables WHERE table_name = '__TtlSweepLock'";

        Assert.That(Convert.ToInt32(await command.ExecuteScalarAsync(ct)), Is.EqualTo(1),
            $"the sweeper should have bootstrapped {TtlSweeper.LockTable}");
    }

    /// <summary>
    /// The grain is hosted, resolvable, and publishes a verdict — the wiring, end to end.
    /// </summary>
    /// <remarks>
    /// <para>Everything else here calls <see cref="TtlSweeper"/> directly, which proves the sweep and
    /// nothing about whether anything in a deployment ever calls it. This goes through the singleton
    /// grain instead: it fails if <c>JobsRole</c> stopped hosting it, if <c>TtlSweepState</c> is not
    /// registered where the grain can take it, or if the constructor asks for something the silo does
    /// not have.</para>
    ///
    /// <para>It runs in whatever mode the test server is configured with, which is the default —
    /// report. A test that flipped the server into apply would be a test that quietly changed the mode
    /// every other fixture runs under.</para>
    /// </remarks>
    [Test, CancelAfter(120_000)]
    public async Task The_sweep_grain_runs_and_publishes_what_it_found(CancellationToken ct = default)
    {
        var before = DateTimeOffset.UtcNow;

        await GetGrainFactory().GetGrain<ITtlSweepGrain>(ITtlSweepGrain.SingletonId).RunSweepAsync();

        var report = FactoryAsp.Services.GetRequiredService<TtlSweepState>().Report;

        Assert.Multiple(() =>
        {
            Assert.That(report.At, Is.GreaterThanOrEqualTo(before), "the grain published nothing");
            Assert.That(report.Verdict, Is.Not.EqualTo(TtlSweepVerdict.Failed), Explain(report));
        });
    }

    /// <summary>
    /// On CockroachDB it does nothing, and it says which engine it found rather than going quiet.
    /// </summary>
    /// <remarks>
    /// <para>Asserted positively, because this is the guard that keeps the two engines from both
    /// deleting the same rows. CockroachDB's own TTL job is rate-limited and range-aware; a second
    /// deleter racing it over the same predicate is work nobody asked for against a cluster that was
    /// already handling it.</para>
    ///
    /// <para>The engine is probed rather than read from <c>Database:Provider</c>, and that matters most
    /// here: the key resolves to <c>CockroachDb</c> when unset or misspelled, so trusting it would make
    /// the sweeper skip a real PostgreSQL deployment on the strength of a typo — the failure that looks
    /// like everything being fine.</para>
    /// </remarks>
    [Test, CancelAfter(120_000)]
    public async Task On_cockroachdb_it_refuses_to_sweep_and_says_which_engine_it_found(CancellationToken ct = default)
    {
        Assume.That(TestEnvironmentOptions.DatabaseKind, Is.EqualTo(TestDatabaseKind.Cockroach),
            "this asserts the no-op path; the PostgreSQL path is asserted by the tests above");

        var report = await SweepAsync(TtlSweepMode.Apply, ct);

        Assert.Multiple(() =>
        {
            Assert.That(report.Verdict, Is.EqualTo(TtlSweepVerdict.NotApplicable));
            Assert.That(report.Description, Does.Contain("CockroachDB"));
            Assert.That(report.Items, Is.Empty, "nothing may be read, let alone deleted");
        });
    }
}
