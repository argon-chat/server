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
/// <para><b>Two of these fail today, on purpose.</b> They are the acceptance criteria for
/// regenerating the migrations, not a claim about the current state. Schema creation runs from the
/// migration files, and those were generated before any entity declared a placement — the snapshot
/// carries <c>Regional:MultiRegion</c> and not one <c>Regional:Locality</c>. So the declarations in
/// <c>ArgonTablePlacement</c> are inert until the migrations are rebuilt, and no amount of correct
/// model configuration changes that. Run them the day the migrations are squashed and they should go
/// green without any other change.</para>
///
/// <para><b>Read the failure, not just the colour.</b> Right-reason red is an assertion diff: the
/// statement came back, and it carries <c>LOCALITY REGIONAL BY TABLE IN PRIMARY REGION</c> — what a
/// multi-region database gives a table nobody placed. Wrong-reason red is anything that never got
/// that far: a rejected <c>LOCALITY</c> clause, or a complaint that the database is not multi-region,
/// means <see cref="CockroachTestDatabase"/> lost its node locality or its primary region and the
/// fixture is reporting on itself. Both of these carried <c>[Explicit]</c> and a comment promising
/// they would pass after the squash; they could not have, because the container they ran against was
/// started without <c>--locality</c> and the DDL under test could not be issued at all. Do not put the
/// attribute back — the squash is a one-way operation against production, and this fixture is the only
/// instrument that can tell a good one from a broken one.</para>
/// </remarks>
[TestFixture, NonParallelizable]
public class TablePlacementTests : TestBase
{
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
    /// This is the property the whole decomposition rests on: a region falling over must not take
    /// sign-in, profiles, roles or the space list with it. Those tables being global is what makes
    /// that true, and it is decided once, in <c>ArgonTablePlacement</c>.
    /// </remarks>
    [Test, CancelAfter(120_000)]
    public async Task Space_and_user_tables_are_global(CancellationToken ct = default)
    {
        OnlyOnCockroach();

        Assert.Multiple(async () =>
        {
            foreach (var table in new[] { "Users", "Spaces", "Channels", "Archetypes" })
                Assert.That(await CreateTableSqlAsync(table, ct), Does.Contain("LOCALITY GLOBAL"),
                    $"'{table}' should be replicated to every region");
        });
    }

    /// <summary>
    /// Messages are homed where they were written, which is the space's region.
    /// </summary>
    /// <remarks>
    /// Nothing carries a region column for that: Cockroach defaults the hidden <c>crdb_region</c> to
    /// <c>gateway_region()</c>, and a channel's rows are only ever inserted by the activation that
    /// owns the channel.
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
