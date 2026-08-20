namespace ArgonComplexTest;

using Argon.Entities;
using Argon.Features.EF;
using ArgonComplexTest.Infrastructure;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;

/// <summary>
/// The reconciler against a server, rather than against a string.
/// </summary>
/// <remarks>
/// <para>The fast suite proves the diff is computed correctly from two records. This one proves the
/// records are the right records: that <c>SHOW CREATE TABLE</c> on a real CockroachDB renders what the
/// parser expects, that the canonical forms on the two sides actually meet, and — the acceptance
/// criterion — that a database the migrations have just built reports back exactly the TTL the model
/// declared. Those are four different ways for this to be subtly wrong and none of them is visible from
/// a unit test.</para>
///
/// <para>Everything here runs in <see cref="SchemaReconcileMode.Report"/>. The tier ceiling is
/// <see cref="SchemaChangeTier.Automatic"/> as well, so even if the mode were wrong the only statement
/// reachable would be a re-pacing one. A test fixture is not a place to find out whether the apply path
/// deletes rows.</para>
/// </remarks>
[TestFixture, NonParallelizable]
public class SchemaTtlReconcilerTests : TestBase
{
    private static void OnlyOnCockroach()
        => Assume.That(TestEnvironmentOptions.DatabaseKind, Is.EqualTo(TestDatabaseKind.Cockroach),
            "row-level TTL is CockroachDB syntax; on PostgreSQL the reconciler refuses by design");

    /// <summary>One read-only pass against the running server.</summary>
    private async Task<SchemaReconcileReport> ReconcileAsync(CancellationToken ct)
    {
        await using var db = await FactoryAsp.Services
           .GetRequiredService<IDbContextFactory<ApplicationDbContext>>()
           .CreateDbContextAsync(ct);

        await db.Database.OpenConnectionAsync(ct);

        return await SchemaReconciler.RunAsync(
            db,
            db.Database.GetDbConnection(),
            new SchemaReconcileOptions(SchemaReconcileMode.Report, TimeSpan.FromMinutes(2)),
            SchemaChangeTier.Automatic,
            roleId: nameof(SchemaTtlReconcilerTests),
            FactoryAsp.Services.GetRequiredService<ILoggerFactory>().CreateLogger("schema-ttl-test"),
            ct);
    }

    private static string Explain(SchemaReconcileReport report)
        => string.Join("\n", report.Plan.Items.Select(item =>
            $"{item.Table} [{item.Status}/{item.Tier}] {item.Reason}{(item.Statement is null ? "" : $" → {item.Statement}")}"));

    /// <summary>
    /// The reader reaches every declared table and understands every answer.
    /// </summary>
    /// <remarks>
    /// The weakest useful claim, and the first one to check when the next test goes red: it separates
    /// "the parser cannot read this server" from "the server and the model genuinely disagree". An
    /// <c>Undetermined</c> item here means the reconciler could not look — which it must never report as
    /// convergence, and which is the reason that verdict exists at all.
    /// </remarks>
    [Test, CancelAfter(120_000)]
    public async Task Every_declared_table_is_read_and_understood(CancellationToken ct = default)
    {
        OnlyOnCockroach();

        var report = await ReconcileAsync(ct);

        Assert.Multiple(() =>
        {
            Assert.That(report.Plan.Items.Select(item => item.Table.Name), Is.EquivalentTo(new[]
            {
                "Invites",
                "TeamInvites",
                "user_friend_requests"
            }));

            Assert.That(report.Plan.HasUndetermined, Is.False, Explain(report));
        });
    }

    /// <summary>
    /// A database the migrations have just built already carries the TTL the model declares.
    /// </summary>
    /// <remarks>
    /// <para>This is the acceptance criterion, and it is the one place the two halves of the defect meet.
    /// The <c>CREATE TABLE</c> path is the only one the generator ever got right, so a freshly migrated
    /// database <em>should</em> already be converged — and the reconciler agreeing with it is what says
    /// its reading of the server is the same reading the generator wrote.</para>
    ///
    /// <para><b>Read the failure, not the colour.</b> Drift here does not mean the database is wrong; it
    /// almost certainly means the canonical forms do not meet — a cron alias the server rendered
    /// differently, a batch parameter it echoes in a shape the parser did not expect. That is the bug
    /// that would otherwise make every production pod emit a pointless <c>ALTER</c> on every boot,
    /// forever, against a database that was correct the whole time. The reason line is printed with the
    /// failure so it can be read directly.</para>
    /// </remarks>
    [Test, CancelAfter(120_000)]
    public async Task A_freshly_migrated_database_carries_the_ttl_the_snapshots_froze(CancellationToken ct = default)
    {
        OnlyOnCockroach();

        var report = await ReconcileAsync(ct);

        var byTable = report.Plan.Items.ToDictionary(item => item.Table.Name);

        Assert.Multiple(() =>
        {
            // The two invite tables are the proof the round trip works: the generator wrote a TTL on
            // the CREATE TABLE path, the reader read it back, and the canonical forms met. If either of
            // these drifts, the normalisation is the suspect and not the database.
            Assert.That(byTable["Invites"].Status,     Is.EqualTo(SchemaTtlStatus.Converged), Explain(report));
            Assert.That(byTable["TeamInvites"].Status, Is.EqualTo(SchemaTtlStatus.Converged), Explain(report));

            // And this one drifts on purpose. FriendRequestEntity declared its TTL against RequestedAt
            // — a column defaulted to now(), so the predicate was true for every row ever written — and
            // that typo is frozen into 47 Designer snapshots. The clause is not a literal in any
            // migration file; Argon's own generator emits it at apply time from the snapshot's
            // annotation, which is why a freshly created database gets it and the long-lived production
            // tables, created before the annotation existed, do not.
            //
            // The model has been repaired to ExpiredAt. Until the snapshots are regenerated the server
            // and the model genuinely disagree, and the reconciler saying so is the correct answer, not
            // a broken test. It lands at Approval because changing the expression re-decides which rows
            // are already expired — exactly the judgement a tier ceiling exists to make.
            Assert.That(byTable["user_friend_requests"].Status, Is.EqualTo(SchemaTtlStatus.Drift), Explain(report));
            Assert.That(byTable["user_friend_requests"].Tier,   Is.EqualTo(SchemaChangeTier.Approval), Explain(report));

            Assert.That(report.Plan.HasUndetermined, Is.False, Explain(report));
        });
    }

    /// <summary>
    /// A second pass finds the same nothing, against the same server.
    /// </summary>
    /// <remarks>
    /// Idempotency is proven from two records in the fast suite; this proves it survives the round trip
    /// through the server's own rendering, which is where a normalisation that only works one way would
    /// show up. Every pod runs this on every boot, so a pass that is not a no-op is a pass that issues
    /// DDL against production forever.
    /// </remarks>
    [Test, CancelAfter(120_000)]
    public async Task A_second_pass_finds_nothing_left_to_do(CancellationToken ct = default)
    {
        OnlyOnCockroach();

        var first  = await ReconcileAsync(ct);
        var second = await ReconcileAsync(ct);

        Assert.Multiple(() =>
        {
            // Same answer twice, rather than "no answer": the plan is not converged while the snapshots
            // still freeze the pre-repair column, and what idempotency means here is that a second pass
            // reports the identical item rather than accumulating or changing its mind.
            Assert.That(second.Plan.Items.Select(item => (item.Table.Name, item.Status)),
                Is.EquivalentTo(first.Plan.Items.Select(item => (item.Table.Name, item.Status))),
                Explain(second));

            Assert.That(second.Applied, Is.Empty, "report mode must never issue a statement");
        });
    }

    /// <summary>
    /// On PostgreSQL it does nothing, and it says so rather than going quiet.
    /// </summary>
    /// <remarks>
    /// <para>Asserted positively rather than skipped, which is the whole point: a reconciler that is
    /// silent on an engine it cannot act on is indistinguishable from one that is broken, and the
    /// default <c>ARGON_TEST_DB</c> is PostgreSQL — so without this, the mode the suite runs in most
    /// often would be the mode nothing checks.</para>
    ///
    /// <para>The engine is probed with <c>DatabaseEngineProbe</c> rather than read from
    /// <c>Database:Provider</c>, and that matters here more than anywhere: the configuration key
    /// resolves to CockroachDb when it is unset <em>or misspelled</em>, so a PostgreSQL deployment set
    /// up from this repository's own documentation announces itself as CockroachDB. This test is what
    /// stops that announcement turning into Cockroach-only DDL sent at PostgreSQL.</para>
    /// </remarks>
    [Test, CancelAfter(120_000)]
    public async Task On_postgresql_it_refuses_to_act_and_says_which_engine_it_found(CancellationToken ct = default)
    {
        Assume.That(TestEnvironmentOptions.DatabaseKind, Is.EqualTo(TestDatabaseKind.Postgres),
            "this asserts the no-op path; the CockroachDB path is asserted by the tests above");

        var report = await ReconcileAsync(ct);

        Assert.Multiple(() =>
        {
            Assert.That(report.Verdict, Is.EqualTo(SchemaReconcileVerdict.NotApplicable));
            Assert.That(report.Description, Does.Contain("PostgreSQL"));
            Assert.That(report.Plan.Items, Is.Empty, "nothing may be read, let alone altered");
        });
    }
}
