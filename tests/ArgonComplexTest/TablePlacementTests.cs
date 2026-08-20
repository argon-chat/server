namespace ArgonComplexTest;

using Argon.Entities;
using Argon.Features.EF;
using ArgonComplexTest.Infrastructure;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;

/// <summary>
/// Where the tables actually ended up, read back from the database that created them.
/// </summary>
/// <remarks>
/// <para>The unit fixture asserts what the SQL generator writes. This one asserts that the writing
/// reached the server and was accepted — a different question, and the one that was open: a
/// <c>LOCALITY</c> clause is emitted only inside <c>CREATE TABLE</c>, so if anything about the
/// creation path bypassed it the annotations would be inert and every table would sit in the primary
/// region with nothing to show for it.</para>
///
/// <para>CockroachDB only, and skipped rather than failed elsewhere. On PostgreSQL the multiregional
/// generator is not installed at all — <c>DatabaseFeature</c> only replaces the generator for
/// Cockroach — so there is nothing to look for. Run with <c>ARGON_TEST_DB=Cockroach</c>.</para>
///
/// <para><b>Two of these fail today, on purpose:</b> <see cref="Space_and_user_tables_are_global"/>
/// and <see cref="Messages_are_regional_by_row"/>. They are the acceptance criteria for the runtime
/// placement reconciler, not a claim about the current state. Schema creation runs from the migration
/// files, and those were generated before any entity declared a placement — the snapshot carries
/// <c>Regional:MultiRegion</c> and not one <c>Regional:Locality</c>. So the declarations in
/// <c>ArgonTablePlacement</c> have never reached any database and no amount of correct model
/// configuration changes that; only <c>ALTER TABLE … SET LOCALITY</c>, issued at runtime against the
/// live catalogue, does. Run these the day the reconciler is allowed to apply and they should go
/// green without any other change. See <c>docs/architecture/table-placement-reconciler.md</c>.</para>
///
/// <para><b>Read the failure, not just the colour.</b> Right-reason red is an assertion diff: the
/// statement came back, and it carries <c>LOCALITY REGIONAL BY TABLE IN PRIMARY REGION</c> — what a
/// multi-region database gives a table nobody placed. Wrong-reason red is anything that never got
/// that far: a rejected <c>LOCALITY</c> clause, or a complaint that the database is not multi-region,
/// means <see cref="CockroachTestDatabase"/> lost its node locality or its primary region and the
/// fixture is reporting on itself. Both of these carried <c>[Explicit]</c> and a comment promising
/// they would pass after a migration squash; they could not have, because the container they ran
/// against was started without <c>--locality</c> and the DDL under test could not be issued at all.
/// Do not put the attribute back — the reconciler applies to production, and this fixture is the only
/// instrument that can tell a good convergence from a broken one.</para>
/// </remarks>
[TestFixture, NonParallelizable]
public class TablePlacementTests : TestBase
{
    /// <summary>What the server reports for a table that was never placed, and for one placed in the
    /// primary region — the two are the same physical state and the same text.</summary>
    private const string DefaultLocality = "LOCALITY REGIONAL BY TABLE IN PRIMARY REGION";

    private async Task<string> CreateTableSqlAsync(string table, CancellationToken ct)
    {
        await using var db = await FactoryAsp.Services
           .GetRequiredService<IDbContextFactory<ApplicationDbContext>>()
           .CreateDbContextAsync(ct);

        await using var command = db.Database.GetDbConnection().CreateCommand();
        await db.Database.OpenConnectionAsync(ct);

        // Quoted: the identifiers are mixed case and an unquoted one is folded to lower.
        command.CommandText = $"SHOW CREATE TABLE \"{table}\"";

        await using var reader = await command.ExecuteReaderAsync(ct);

        // SHOW CREATE TABLE answers with (table_name, create_statement).
        return await reader.ReadAsync(ct) ? reader.GetString(1) : "";
    }

    private static void OnlyOnCockroach()
        => Assume.That(TestEnvironmentOptions.DatabaseKind, Is.EqualTo(TestDatabaseKind.Cockroach),
            "table placement is CockroachDB syntax; the generator is not even installed on PostgreSQL");

    /// <summary>
    /// The tables a user needs to sign in and see their world, readable from every region.
    /// </summary>
    /// <remarks>
    /// <para>This is the property the whole decomposition rests on: a region falling over must not
    /// take sign-in, profiles, roles or the space list with it. Those tables being global is what
    /// makes that true, and it is decided once, in <c>ArgonTablePlacement</c>.</para>
    ///
    /// <para>Five, not ten. The audit (§5b) rules that a global table is only paid for by data
    /// written per lifecycle, and six of the ten original declarations were written on a user-facing
    /// action. What survives is the reference data the feature is documented for — an account, its
    /// profile, a space's metadata, a channel's metadata, and the roles every permission evaluation
    /// reads.</para>
    ///
    /// <para><c>Channels</c> was one of the six and is back, because the write that disqualified it
    /// was removed rather than argued away: <c>LastMessageId</c> moved to <c>ChannelLastMessages</c>,
    /// which the fixture below expects to find homed in one region. If that ever regressed — a writer
    /// touching the channel row's counter again — this line would be asserting a commit-wait onto the
    /// message path, which is the most expensive place in the product to put one.</para>
    /// </remarks>
    [Test, CancelAfter(120_000)]
    public async Task Space_and_user_tables_are_global(CancellationToken ct = default)
    {
        OnlyOnCockroach();

        Assert.Multiple(async () =>
        {
            foreach (var table in new[] { "Users", "UserProfiles", "Spaces", "Archetypes", "Channels" })
                Assert.That(await CreateTableSqlAsync(table, ct), Does.Contain("LOCALITY GLOBAL"),
                    $"'{table}' should be replicated to every region");
        });
    }

    /// <summary>
    /// And the tables written by a click, or by a message, stay in one region.
    /// </summary>
    /// <remarks>
    /// <para>Every table here is written on something a person just did — a join, a role grant, a
    /// permission toggle, an accepted invite, a drag-and-drop reorder, or sending a message
    /// (<c>ChannelLastMessages</c>) — and <c>LOCALITY GLOBAL</c> charges each of those a commit-wait
    /// of a few hundred milliseconds. The declarations that said otherwise were never applied to a
    /// database, which is the only reason this was a correctable mistake rather than an
    /// incident.</para>
    ///
    /// <para><c>ChannelLastMessages</c> is the newest of them and the only one here that was created
    /// after the placement annotations existed, which means it is also the only one whose declaration
    /// really did reach the server: <c>LOCALITY</c> is emitted inside <c>CREATE TABLE</c>, and this
    /// table had one. It reports the same clause as the tables nobody ever placed, which is the point
    /// — the two are the same physical state.</para>
    ///
    /// <para><b>This one passes today, and that is not it being weak.</b> A table converged to
    /// <c>REGIONAL BY TABLE</c> and a table nobody ever placed report the identical clause — that
    /// equivalence is exactly why the reconciler emits no statement for these six and why the run
    /// after it is empty. What this test catches is the day somebody moves one of them back into the
    /// global block and the reconciler applies it: the assertion then reads
    /// <c>LOCALITY GLOBAL</c> where it wanted the default, and names the table. It is also the
    /// fixture's tripwire for a container that came up without a primary region — then the statement
    /// carries no <c>LOCALITY</c> at all and every line here fails at once, which is the
    /// wrong-reason red described above.</para>
    /// </remarks>
    [Test, CancelAfter(120_000)]
    public async Task Interactively_written_tables_are_homed_in_one_region(CancellationToken ct = default)
    {
        OnlyOnCockroach();

        // ChannelGroupEntity has no ToTable and no DbSet, so its table is literally the class name.
        var tables = new[]
        {
            "ChannelLastMessages", "ChannelGroupEntity", "UsersToServerRelations",
            "MemberArchetypes", "ChannelEntitlementOverwrites", "Invites"
        };

        Assert.Multiple(async () =>
        {
            foreach (var table in tables)
                Assert.That(await CreateTableSqlAsync(table, ct), Does.Contain(DefaultLocality),
                    $"'{table}' is written interactively and must not pay a commit-wait for it");
        });
    }

    /// <summary>
    /// Messages are homed where they were written, which is the space's region.
    /// </summary>
    /// <remarks>
    /// <para>Nothing carries a region column for that: Cockroach defaults the hidden
    /// <c>crdb_region</c> to <c>gateway_region()</c>, and a channel's rows are only ever inserted by
    /// the activation that owns the channel.</para>
    ///
    /// <para>This is the last of these to go green, and deliberately so. The conversion is not a
    /// metadata change — <c>SET LOCALITY REGIONAL BY ROW</c> is implemented as an
    /// <c>ALTER PRIMARY KEY</c> and rewrites every index on the largest table in the product — so it
    /// is an operator-run migration rather than something the boot path is ever allowed to do. Red
    /// here is not a reason to delete the declaration; it is the reminder that §6 has not been
    /// executed yet.</para>
    /// </remarks>
    [Test, CancelAfter(120_000)]
    public async Task Messages_are_regional_by_row(CancellationToken ct = default)
    {
        OnlyOnCockroach();

        Assert.That(await CreateTableSqlAsync("Messages", ct), Does.Contain("REGIONAL BY ROW"));
    }

    /// <summary>
    /// And a table nobody placed keeps the default rather than acquiring one by accident.
    /// </summary>
    /// <remarks>
    /// Most of the fifty-odd tables are unplaced on purpose — the decomposition decided eleven of
    /// them and left the rest where they were. If a table started showing up as global without
    /// anyone asking, the placement block would have grown a wildcard.
    /// </remarks>
    [Test, CancelAfter(120_000)]
    public async Task An_unplaced_table_keeps_the_default(CancellationToken ct = default)
    {
        OnlyOnCockroach();

        Assert.That(await CreateTableSqlAsync("DeviceHistories", ct), Does.Not.Contain("LOCALITY GLOBAL"));
    }
}
