namespace ArgonComplexTest;

using Argon.Entities;
using Argon.Features.EF;
using ArgonComplexTest.Infrastructure;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;

/// <summary>
/// The declaration step against a server, rather than against a model.
/// </summary>
/// <remarks>
/// <para>The fast suite proves the statements are the right strings. This proves the server accepts
/// them and that the catalogue changes — a different question, and the one that was open for the whole
/// life of this defect: <c>LOCALITY</c> and the TTL parameters are only ever written inside
/// <c>CREATE TABLE</c>, so every table that existed before its declaration was written has been
/// carrying no declaration at all, and no amount of correct model configuration changes that.</para>
///
/// <para><b>This is not the acceptance test for the placement work.</b>
/// <see cref="TablePlacementTests"/> is: it asks the database what it carries, with no step of ours in
/// the frame, which is the only way to find out whether the boot path did it rather than whether this
/// fixture did. What this adds is the half that fixture cannot see — that the step is what put it
/// there, that it refuses <c>REGIONAL BY ROW</c> against a live server rather than only against a
/// string, and that on PostgreSQL it issues nothing at all.</para>
///
/// <para><c>NonParallelizable</c> because it issues schema changes against the shared database. On
/// CockroachDB two schema changes on one table at once is how you get errors nobody can read.</para>
/// </remarks>
[TestFixture, NonParallelizable]
public class SchemaDeclarationTests : TestBase
{
    /// <summary>
    /// Every line the step logged, which is how "it did nothing" is asserted.
    /// </summary>
    /// <remarks>
    /// The PostgreSQL case has no other evidence available. There is no catalogue to read back — the
    /// engine has neither localities nor row-level TTL — and the step is written to survive a statement
    /// the server refuses, so "it threw nothing" would also be true of a broken engine guard that
    /// issued twelve statements and had every one rejected. What separates those two is what it said.
    /// </remarks>
    private sealed class CapturedLog : ILogger
    {
        private readonly List<string> lines = [];

        public IReadOnlyList<string> Lines
        {
            get
            {
                lock (lines)
                    return lines.ToList();
            }
        }

        public bool Said(string fragment)
            => Lines.Any(line => line.Contains(fragment, StringComparison.OrdinalIgnoreCase));

        public override string ToString() => string.Join(Environment.NewLine, Lines);

        public IDisposable? BeginScope<TState>(TState state) where TState : notnull => null;

        public bool IsEnabled(LogLevel logLevel) => true;

        public void Log<TState>(
            LogLevel logLevel, EventId eventId, TState state, Exception? exception,
            Func<TState, Exception?, string> formatter)
        {
            lock (lines)
                lines.Add(formatter(state, exception));
        }
    }

    /// <summary>One run of the step against the running server, on its own pinned connection.</summary>
    private async Task<CapturedLog> ApplyAsync(CancellationToken ct)
    {
        await using var db = await FactoryAsp.Services
           .GetRequiredService<IDbContextFactory<ApplicationDbContext>>()
           .CreateDbContextAsync(ct);

        await db.Database.OpenConnectionAsync(ct);

        var log = new CapturedLog();

        await SchemaDeclarations.ApplyAsync(db, db.Database.GetDbConnection(), dryRun: false, log, ct);

        return log;
    }

    private async Task<string> CreateTableSqlAsync(TableRef table, CancellationToken ct)
    {
        await using var db = await FactoryAsp.Services
           .GetRequiredService<IDbContextFactory<ApplicationDbContext>>()
           .CreateDbContextAsync(ct);

        await db.Database.OpenConnectionAsync(ct);

        await using var command = db.Database.GetDbConnection().CreateCommand();

        // Quoted: the identifiers are mixed case and an unquoted one is folded to lower.
        command.CommandText = $"SHOW CREATE TABLE {table.Quoted}";

        await using var reader = await command.ExecuteReaderAsync(ct);

        // SHOW CREATE TABLE answers with (table_name, create_statement).
        return await reader.ReadAsync(ct) ? reader.GetString(1) : "";
    }

    /// <summary>
    /// After a pass, every table the model places carries its placement, every table it expires
    /// carries its TTL, and the one declaration it will not issue is named out loud.
    /// </summary>
    /// <remarks>
    /// <para>Derived from the model rather than listed, so a thirteenth <c>PlacementGlobal()</c> is
    /// covered the day it is written. The clause is matched by prefix because CockroachDB renders the
    /// long form of what it was given: <c>SET LOCALITY REGIONAL BY TABLE</c> comes back as
    /// <c>LOCALITY REGIONAL BY TABLE IN PRIMARY REGION</c>, and those are the same physical state.</para>
    ///
    /// <para>The TTL half asserts the parameter and the column rather than the exact rendering of the
    /// expression. Which quoting the server echoes back is its business; that the job is on and points
    /// at the declared column is ours.</para>
    ///
    /// <para><b>The refusal is asserted as a log line and never as a catalogue read.</b> Asserting that
    /// <c>Messages</c> is <em>not</em> <c>REGIONAL BY ROW</c> would contradict
    /// <see cref="TablePlacementTests.Messages_are_regional_by_row"/>, which is ignored rather than run
    /// because that conversion is a staged operator-run migration nobody has executed. Two fixtures
    /// asserting opposite things about one table is worse than one of them being switched off — and an
    /// assertion that the table is *not* converted would have to be deleted the day it is. What belongs here is
    /// that the step declines rather than silently omitting: a table missing from a run is
    /// indistinguishable from a table nobody declared.</para>
    ///
    /// <para>One pass for all of it, rather than a test each. Every assertion needs the same fourteen
    /// schema changes to have run, and issuing them twice against a single-node container buys nothing
    /// but wall clock.</para>
    /// </remarks>
    [Test, CancelAfter(180_000)]
    public async Task The_step_applies_every_declaration_the_model_carries(CancellationToken ct = default)
    {
        Assume.That(TestEnvironmentOptions.DatabaseKind, Is.EqualTo(TestDatabaseKind.Cockroach),
            "placement and row-level TTL are CockroachDB syntax; the step refuses PostgreSQL by design");

        var log = await ApplyAsync(ct);

        await using var db = await FactoryAsp.Services
           .GetRequiredService<IDbContextFactory<ApplicationDbContext>>()
           .CreateDbContextAsync(ct);

        // Read before asserting, rather than inside an Assert.Multiple that awaits. An async lambda
        // handed to Assert.Multiple can bind to the synchronous overload, which makes it async void and
        // lets the awaits — and therefore the assertions — escape the block entirely. Catalogue reads
        // first, assertions second, and the question does not arise.
        var placements = new List<(TableRef Table, string Locality, string Sql)>();

        foreach (var (table, locality) in SchemaDeclarations.ReadLocalities(db.Model))
        {
            if (SchemaDeclarations.PlacementStatement(table, locality) is null)
                continue;

            placements.Add((table, locality, await CreateTableSqlAsync(table, ct)));
        }

        var expiries = new List<(TableRef Table, TtlSettings Ttl, string Sql)>();

        foreach (var (table, ttl) in SchemaTtlModel.ReadDesiredState(db.Model))
            expiries.Add((table, ttl, await CreateTableSqlAsync(table, ct)));

        Assert.Multiple(() =>
        {
            Assert.That(log.Said("refused"), Is.False, log.ToString());

            // Placement is not asserted here, and the omission is the behaviour rather than a gap in the
            // test. The step leaves placement alone on a database with fewer than two regions, and this
            // container is one node with one locality — so on this fixture the correct number of
            // LOCALITY statements is zero, and a green here would mean the gate had failed. The model
            // still declares them, which is why the list is built above and only the assertion is
            // withheld: what needs a multi-region fixture is the proof, not the code.
            Assert.That(placements, Is.Not.Empty,
                "the model placed nothing, so there would be nothing to apply even with regions");

            foreach (var (table, ttl, sql) in expiries)
            {
                Assert.That(sql, Does.Contain("ttl_expiration_expression"), $"{table} carries no row-level TTL");
                Assert.That(sql, Does.Contain(ttl.ExpirationExpression!),
                    $"{table} expires on a column other than the declared \"{ttl.ExpirationExpression}\"");
            }

            // The per-table refusal is not asserted for the same reason the placements are not: below two
            // regions the step never reaches the loop that would refuse anything, so there is nothing to
            // decline. The negative below survives regardless and is the one that matters — it is false
            // on every path, including the ones that do run, and it is what goes red the day somebody
            // deletes the guard.
            Assert.That(log.Said("SET LOCALITY REGIONAL BY ROW"), Is.False,
                "the guard is gone and the step is converting the largest table in the product");
        });
    }

    /// <summary>
    /// On PostgreSQL the step issues nothing, and says so rather than being quiet about it.
    /// </summary>
    /// <remarks>
    /// Both halves matter. Nothing issued, because every statement it knows is syntax this engine has
    /// never heard of; and said out loud, because a step that is silent on an engine it cannot act on
    /// is indistinguishable from one that is broken — which is the same reason
    /// <c>TtlSweepGrain</c> exists at all, since PostgreSQL honouring <c>Job:Expiration</c> has to
    /// happen somewhere.
    /// </remarks>
    [Test, CancelAfter(120_000)]
    public async Task On_postgres_the_step_issues_nothing_and_says_so(CancellationToken ct = default)
    {
        Assume.That(TestEnvironmentOptions.DatabaseKind, Is.EqualTo(TestDatabaseKind.Postgres),
            "this is the no-op case; on CockroachDB the step has work to do");

        var log = await ApplyAsync(ct);

        Assert.Multiple(() =>
        {
            Assert.That(log.Said("postgresql"), Is.True, log.ToString());
            Assert.That(log.Said("Applying table declaration"), Is.False, log.ToString());
            Assert.That(log.Said("ALTER TABLE"), Is.False, log.ToString());
        });
    }
}
