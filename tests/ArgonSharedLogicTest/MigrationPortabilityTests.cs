namespace ArgonSharedLogicTest;

using Argon.Core.Features.EF;
using System.Reflection;
using System.Text.RegularExpressions;

/// <summary>
/// One migration history, two database engines.
/// </summary>
/// <remarks>
/// <para>Argon runs on CockroachDB in production and on vanilla PostgreSQL in single-region
/// deployments and in the integration suite. Both replay the same migration files, which only works
/// because every CockroachDB-ism is handled by one of exactly two mechanisms:</para>
///
/// <list type="number">
/// <item><b>A function or expression</b> — taught to PostgreSQL by
/// <see cref="PostgresCompatibilityShims"/>, which runs before the first migration. Ninety-seven
/// column defaults call <c>unique_rowid()</c>; defining it is what lets both engines execute the
/// same history byte for byte.</item>
/// <item><b>A DDL clause</b> — <c>LOCALITY</c>, <c>PRIMARY REGION</c>, <c>SURVIVE</c>, the TTL
/// storage parameters. PostgreSQL cannot be taught those, so they never appear in a migration file
/// at all: the migration carries an annotation and
/// <c>MultiregionalMigrationsSqlGenerator</c> — installed only for CockroachDB — turns it into SQL at
/// apply time.</item>
/// </list>
///
/// <para>Both rules are invisible while they hold and expensive when they break: a Cockroach-only
/// function in a column default passes every Cockroach test and fails on PostgreSQL at the first
/// migration, on a machine nobody was watching. So they are checked here rather than remembered.</para>
/// </remarks>
[TestFixture]
public class MigrationPortabilityTests
{
    /// <summary>Functions CockroachDB has and PostgreSQL does not, so they need a shim.</summary>
    private static readonly string[] CockroachFunctions =
    [
        "unique_rowid", "crdb_internal", "cluster_logical_timestamp", "experimental_uuid_v4",
        "gateway_region", "crdb_region"
    ];

    /// <summary>
    /// Clauses that must never reach a migration file.
    /// </summary>
    /// <remarks>
    /// There is no shimming a syntax error. These are generated from annotations, conditionally, by a
    /// generator PostgreSQL never sees — writing one into a migration by hand makes that migration
    /// un-appliable on PostgreSQL forever.
    ///
    /// <para>The list covers the whole TTL vocabulary rather than the two parameters that happened to
    /// be here first, and the reason is <c>SchemaDeclarations</c>. It issues these clauses itself, on
    /// the boot path, behind an engine check — which makes them exactly the strings somebody will have
    /// in front of them while writing SQL, and the pull towards pasting one into a migration is
    /// strongest when a working statement is already on screen. This scan reads only
    /// <c>src/Argon.Core/Migrations/</c>, so it does not and must not bind that file.
    /// <c>SET LOCALITY</c> is listed even though the two <c>LOCALITY</c> entries already catch every
    /// form of it: an explicit entry is what a reader greps for when they want to know whether this is
    /// allowed.</para>
    /// </remarks>
    private static readonly string[] CockroachClauses =
    [
        "LOCALITY GLOBAL", "LOCALITY REGIONAL", "SET LOCALITY", "PRIMARY REGION", "SURVIVE REGION",
        "SURVIVE ZONE", "AS OF SYSTEM TIME", "INVERTED INDEX", "SPLIT AT",
        "ttl_expiration_expression", "ttl_job_cron", "ttl_expire_after", "ttl_automatic_column",
        "ttl_select_batch_size", "ttl_delete_batch_size", "ttl_delete_rate_limit", "ttl_pause",
        "ttl = 'on'", "RESET (ttl"
    ];

    private static DirectoryInfo RepositoryDirectory(params string[] parts)
    {
        var directory = new DirectoryInfo(TestContext.CurrentContext.TestDirectory);

        while (directory is not null && !File.Exists(Path.Combine(directory.FullName, "Argon.Server.slnx")))
            directory = directory.Parent;

        Assert.That(directory, Is.Not.Null, "could not find the repository root from the test directory");

        var target = new DirectoryInfo(Path.Combine([directory!.FullName, .. parts]));

        Assert.That(target.Exists, Is.True, $"nothing at '{target.FullName}'");
        return target;
    }

    private static DirectoryInfo MigrationsDirectory()
        => RepositoryDirectory("src", "Argon.Core", "Migrations");

    /// <summary>
    /// The SQL a migration file contains, which is its string literals and nothing else.
    /// </summary>
    /// <remarks>
    /// Scanning the whole file finds the word "survive" in a comment about surviving a partial
    /// re-run, which is how the first version of this failed. Only a literal can reach the database,
    /// so only a literal is evidence.
    /// </remarks>
    private static string LiteralsOf(string source)
    {
        var raw     = Regex.Matches(source, "\"{3,}.*?\"{3,}", RegexOptions.Singleline);
        var regular = Regex.Matches(source, "\"(?:[^\"\\\\\n]|\\\\.)*\"");

        return string.Join("\n", raw.Concat(regular).Select(m => m.Value));
    }

    /// <summary>
    /// The migration files themselves, without the designer snapshots.
    /// </summary>
    /// <remarks>
    /// The snapshots carry the model's annotations — <c>Regional:MultiRegion</c> among them — as C#
    /// strings, which are not SQL and are exactly how a clause is <em>supposed</em> to travel. Scanning
    /// them would flag the mechanism that makes this work.
    /// </remarks>
    private static IEnumerable<FileInfo> MigrationFiles()
        => MigrationsDirectory()
           .EnumerateFiles("*.cs")
           .Where(f => !f.Name.EndsWith(".Designer.cs", StringComparison.Ordinal))
           .Where(f => !f.Name.EndsWith("ModelSnapshot.cs", StringComparison.Ordinal));

    /// <summary>Every function PostgreSQL is taught, read out of the shims themselves.</summary>
    /// <remarks>
    /// Read rather than listed so that adding a shim is the whole of adding a shim. A second list
    /// here would be a second place to forget.
    /// </remarks>
    private static HashSet<string> ShimmedFunctions()
    {
        var sql = string.Join("\n", typeof(PostgresCompatibilityShims)
           .GetFields(BindingFlags.Public | BindingFlags.Static)
           .Where(f => f is { IsLiteral: true, FieldType.Name: nameof(String) })
           .Select(f => (string)f.GetRawConstantValue()!));

        return Regex.Matches(sql, @"CREATE\s+(?:OR\s+REPLACE\s+)?FUNCTION\s+([A-Za-z_][A-Za-z0-9_]*)",
                RegexOptions.IgnoreCase)
           .Select(m => m.Groups[1].Value)
           .ToHashSet(StringComparer.OrdinalIgnoreCase);
    }

    [Test]
    public void The_shims_define_something()
        => Assert.That(ShimmedFunctions(), Does.Contain("unique_rowid"),
            "if this is empty the scan below cannot fail, and would be worse than no test");

    /// <summary>
    /// Every CockroachDB function a migration calls has an equivalent taught to PostgreSQL.
    /// </summary>
    /// <remarks>
    /// The failure this prevents is the quiet one: the migration applies on Cockroach, the suite is
    /// green because it runs on Cockroach that day, and the first PostgreSQL deployment stops at
    /// <c>function … does not exist</c> having applied half the history.
    /// </remarks>
    [Test]
    public void A_cockroach_function_in_a_migration_is_shimmed_for_postgres()
    {
        var shimmed = ShimmedFunctions();
        var missing = new List<string>();

        foreach (var file in MigrationFiles())
        {
            var text = LiteralsOf(File.ReadAllText(file.FullName));

            missing.AddRange(
                from function in CockroachFunctions
                where !shimmed.Contains(function)
                where text.Contains(function, StringComparison.OrdinalIgnoreCase)
                select $"{file.Name} calls '{function}'");
        }

        Assert.That(missing, Is.Empty,
            "add the function to PostgresCompatibilityShims, or stop using it in a migration:"
          + Environment.NewLine + string.Join(Environment.NewLine, missing));
    }

    /// <summary>
    /// And no migration writes a clause PostgreSQL cannot parse.
    /// </summary>
    /// <remarks>
    /// These belong in model annotations, which the CockroachDB-only generator turns into SQL and the
    /// PostgreSQL one ignores. A clause written straight into a migration has no such off switch.
    /// </remarks>
    [Test]
    public void No_migration_writes_a_cockroach_only_clause()
    {
        var offenders = new List<string>();

        foreach (var file in MigrationFiles())
        {
            var text = LiteralsOf(File.ReadAllText(file.FullName));

            offenders.AddRange(
                from clause in CockroachClauses
                where text.Contains(clause, StringComparison.OrdinalIgnoreCase)
                select $"{file.Name} contains '{clause}'");
        }

        Assert.That(offenders, Is.Empty,
            "declare it on the model instead and let MultiregionalMigrationsSqlGenerator emit it:"
          + Environment.NewLine + string.Join(Environment.NewLine, offenders));
    }

    /// <summary>
    /// The same rule one step earlier, where the SQL is actually written.
    /// </summary>
    /// <remarks>
    /// <para>A migration is scaffolded from the model, so a Cockroach built-in put into a
    /// <c>HasDefaultValueSql</c> today becomes a migration tomorrow — and the scan above would only
    /// notice after it had been baked into the history, which is the point at which rewriting it
    /// stops being free. <c>unique_rowid()</c> is how the ninety-seven column defaults got there.</para>
    ///
    /// <para>Catching it in the entity configuration means the answer is still cheap: add a shim, or
    /// use something both engines have.</para>
    /// </remarks>
    [Test]
    public void A_cockroach_function_in_an_entity_configuration_is_shimmed_for_postgres()
    {
        var shimmed = ShimmedFunctions();
        var missing = new List<string>();

        foreach (var file in RepositoryDirectory("src", "Argon.Core", "Entities").EnumerateFiles("*.cs", SearchOption.AllDirectories))
        {
            var text = LiteralsOf(File.ReadAllText(file.FullName));

            missing.AddRange(
                from function in CockroachFunctions
                where !shimmed.Contains(function)
                where text.Contains(function, StringComparison.OrdinalIgnoreCase)
                select $"{file.Name} uses '{function}'");
        }

        Assert.That(missing, Is.Empty,
            "add the function to PostgresCompatibilityShims before this reaches a migration:"
          + Environment.NewLine + string.Join(Environment.NewLine, missing));
    }

    /// <summary>
    /// The scan is looking at something.
    /// </summary>
    /// <remarks>
    /// A path that silently resolves to an empty directory would make both tests above pass forever.
    /// </remarks>
    [Test]
    public void There_are_migrations_to_scan()
        => Assert.That(MigrationFiles().Count(), Is.GreaterThan(0));
}
