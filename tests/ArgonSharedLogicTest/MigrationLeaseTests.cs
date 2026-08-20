namespace ArgonSharedLogicTest;

using Argon.Core.Features.EF;
using Argon.Features.EF;
using System.Reflection;
using System.Text.RegularExpressions;

/// <summary>
/// The migration lock, checked without a database — which is where most of it can be checked.
/// </summary>
/// <remarks>
/// <para>The boot path used to hand-roll a lock over <c>__MigrationLock</c> with four defects: an
/// <c>expires_at</c> computed on the pod's clock and compared against the server's, a release of
/// <c>DELETE … WHERE id = 1</c> with no owner predicate running in a <c>finally</c>, no fence, and no
/// renewal on a ten-minute TTL. It now takes <see cref="SchemaReconcileLease"/> — the same lease the
/// schema reconciler and the TTL sweeper hold — so what is left to check here is that the boot path
/// really does take it, over a resource of its own, and that the lease's statements still say what the
/// boot path is now trusting them to say.</para>
///
/// <para>The integration suite proves the behaviour against a live engine. This proves the parts that
/// are decided at compile time and would otherwise only fail on a pod, at boot, in a fleet — which is
/// the most expensive place to find out that a lock table name has a hyphen in it.</para>
/// </remarks>
[TestFixture]
public class MigrationLeaseTests
{
    /// <summary>The table the boot path takes, named once so a rename shows up as one failure.</summary>
    private const string BootLease = WarmUpExtension.MigrationLeaseTable;

    /// <summary>
    /// The table the old hand-rolled lock used, and the reason the boot path does not point the lease
    /// at it.
    /// </summary>
    /// <remarks>
    /// It exists in every deployed database already, with <c>(id, locked_at, locked_by, expires_at)</c>
    /// and no <c>fence</c>. The lease bootstraps with <c>CREATE TABLE IF NOT EXISTS</c>, which adds no
    /// column to a table that is already there, so reusing this name would put an <c>INSERT</c> naming
    /// a missing column on the boot path of every pod at once.
    /// </remarks>
    private const string AbandonedLock = "__MigrationLock";

    /// <summary>A name the lease is asked to build SQL for, so the statements can be read.</summary>
    private const string Probe = "__LeaseProbe";

    #region which row the boot path takes

    /// <summary>
    /// The lease will accept the name the boot path hands it.
    /// </summary>
    /// <remarks>
    /// <see cref="SchemaReconcileLease"/> validates its table name rather than trusting it, because
    /// that name is the one string it concatenates into DDL — and it validates by throwing. A name with
    /// a hyphen, a space or a quote in it would therefore not be a bad lock: it would be an
    /// <see cref="ArgumentException"/> on the first line of warm-up, on every pod, and the fleet would
    /// not start. The regex is the lease's own, restated here because the lease keeps its private.
    /// </remarks>
    [Test]
    public void The_boot_lease_table_is_a_name_the_lease_will_accept()
        => Assert.That(BootLease, Does.Match(@"^[A-Za-z_][A-Za-z0-9_$]*$"),
            $"'{BootLease}' would make SchemaReconcileLease.Delimit throw during warm-up, on every pod");

    /// <summary>
    /// Three resources, three rows.
    /// </summary>
    /// <remarks>
    /// Sharing one row between two jobs is not a stronger lock, it is a wrong answer. If migrations and
    /// the schema reconciler took the same row, the reconcile pass that runs a few lines after the
    /// migrations — inside the same boot, while the migration lease is still held — would find it busy
    /// and publish <c>SkippedLock</c>, a verdict that means "another worker is converging the schema"
    /// and would be reported because this pod was holding its own lock. Sharing with the TTL sweeper
    /// has the mirror-image failure: an hourly delete pass could stop a pod migrating.
    /// </remarks>
    [Test]
    public void Three_lease_tables_and_no_two_of_them_are_the_same()
    {
        string[] tables = [BootLease, SchemaReconcileLease.DefaultLockTable, TtlSweeper.LockTable];

        Assert.That(tables, Is.Unique,
            $"migrations, the schema reconciler and the TTL sweeper take {string.Join(", ", tables)} — "
          + "two of those are the same row, so one job can lock the other out of a resource it does "
          + "not touch");
    }

    /// <summary>
    /// And the boot path's row is not the one that is already out there.
    /// </summary>
    /// <remarks>
    /// This is the whole of the migration decision in one assertion, and it is worth a test rather than
    /// a comment because the pull towards "just reuse the table that exists" is strong and the cost of
    /// giving in to it is <c>42703: column "fence" does not exist</c> on the boot path of every pod of
    /// every role, simultaneously, on the first deploy that carries the change. Abandoning
    /// <c>__MigrationLock</c> costs one dead table that an operator can drop by hand at any time.
    /// </remarks>
    [Test]
    public void The_boot_lease_is_not_the_table_that_already_exists_without_a_fence()
        => Assert.That(BootLease, Is.Not.EqualTo(AbandonedLock),
            "the deployed __MigrationLock has no fence column and CREATE TABLE IF NOT EXISTS will not "
          + "add one; a new table is the only way to bootstrap this on a boot path");

    #endregion

    #region the statements the boot path is trusting

    /// <summary>
    /// The lease's statements, read out of the lease.
    /// </summary>
    /// <remarks>
    /// Reflection, and deliberately. These statements are assembled from strings and compiled by
    /// nothing — the only two ways to find out what they say are to run them against a live engine,
    /// which the integration suite does and which costs containers, or to read them. Every property
    /// this file asserts is visible in the text and nowhere else. If a rename makes this fail, the
    /// failure names the method it wanted.
    /// </remarks>
    private static string Statement(string builder)
    {
        var method = typeof(SchemaReconcileLease)
           .GetMethod(builder, BindingFlags.NonPublic | BindingFlags.Static);

        Assert.That(method, Is.Not.Null,
            $"SchemaReconcileLease.{builder}(string) is gone or renamed; this test reads it by name");

        return (string)method!.Invoke(null, [Probe])!;
    }

    /// <summary>
    /// Acquiring compares the server's clock with the server's clock.
    /// </summary>
    /// <remarks>
    /// The defect this replaces computed <c>expires_at</c> from the pod's <c>DateTime.UtcNow</c> and
    /// then compared it against the server's <c>now()</c> in the steal predicate. A pod running ahead
    /// held the lock long past the TTL; one running behind had it stolen while it was still migrating.
    /// Both sides being <c>now()</c> is what makes the TTL mean the same thing to every pod, and it is
    /// invisible unless somebody looks at the statement.
    /// </remarks>
    [Test]
    public void Acquiring_reads_the_clock_from_the_server_on_both_sides()
    {
        var sql = Statement("AcquireSql");

        Assert.Multiple(() =>
        {
            Assert.That(sql, Does.Contain("now() + @ttl"),
                "the expiry must be computed on the server, not sent as a client timestamp");
            Assert.That(sql, Does.Contain("expires_at < now()"),
                "the steal predicate must compare against the server's clock");
            Assert.That(sql, Does.Contain("fence"),
                "the acquire must move the fence, or a stalled holder cannot tell its tenure ended");
        });
    }

    /// <summary>
    /// Renewing and releasing both name the holder and the fence.
    /// </summary>
    /// <remarks>
    /// <para>The release predicate is the fix for the worst of the four defects. The old release was
    /// <c>DELETE FROM "__MigrationLock" WHERE id = 1</c> in a <c>finally</c>: a worker whose lease had
    /// already expired and been taken deleted the <em>new</em> holder's row on its way out, and the
    /// next arrival then acquired an empty table freely — two workers applying migrations at once.</para>
    ///
    /// <para>The renew predicate is what makes the stop possible at all. Predicated the same way, it
    /// updates no rows once the tenure has ended, which is the signal <c>ApplyMigrationsAsync</c> turns
    /// into a refusal to keep going.</para>
    /// </remarks>
    [Test]
    public void Renewing_and_releasing_are_both_predicated_on_holder_and_fence()
    {
        var renew   = Statement("RenewSql");
        var release = Statement("ReleaseSql");

        Assert.Multiple(() =>
        {
            Assert.That(renew, Does.Contain("locked_by = @holder"));
            Assert.That(renew, Does.Contain("fence = @fence"));
            Assert.That(release, Does.Contain("locked_by = @holder"),
                "an unqualified release deletes whoever holds the lease now");
            Assert.That(release, Does.Contain("fence = @fence"),
                "holder alone is not enough: two tenures of the same process share a holder id");
        });
    }

    #endregion

    #region and the boot path keeps none of its own

    private static FileInfo WarmUpSource()
    {
        // Walked rather than configured, exactly as MigrationPortabilityTests does. Copied rather than
        // shared because two four-line helpers are cheaper than a test-support assembly, and because
        // the day one of them needs to look somewhere else it should be free to.
        var directory = new DirectoryInfo(TestContext.CurrentContext.TestDirectory);

        while (directory is not null && !File.Exists(Path.Combine(directory.FullName, "Argon.Server.slnx")))
            directory = directory.Parent;

        Assert.That(directory, Is.Not.Null, "could not find the repository root from the test directory");

        var file = new FileInfo(Path.Combine(
            directory!.FullName, "src", "Argon.Core", "Features", "EF", "WarmUpExtensions.cs"));

        Assert.That(file.Exists, Is.True, $"nothing at '{file.FullName}'");

        return file;
    }

    /// <summary>
    /// The SQL a source file contains, which is its string literals outside comments and nothing else.
    /// </summary>
    /// <remarks>
    /// Two filters and both are needed. Only a literal can reach the database, so scanning the whole
    /// file would flag the doc comment that explains why <c>__MigrationLock</c> was abandoned — the
    /// comment this test exists to keep true. And that comment quotes the old table name with real
    /// double quotes, so stripping comments has to happen before literals are extracted rather than
    /// after.
    /// </remarks>
    private static string SqlLiteralsOf(string source)
    {
        var code = Regex.Replace(source, @"^[ \t]*//.*$", "", RegexOptions.Multiline);

        var raw     = Regex.Matches(code, "\"{3,}.*?\"{3,}", RegexOptions.Singleline);
        var regular = Regex.Matches(code, "\"(?:[^\"\\\\\n]|\\\\.)*\"");

        return string.Join("\n", raw.Concat(regular).Select(match => match.Value));
    }

    /// <summary>
    /// Warm-up issues no lock SQL of its own any more.
    /// </summary>
    /// <remarks>
    /// <para>The regression guard for the defect that mattered, and the reason it is a source scan
    /// rather than a behavioural test: the behaviour of a correct lease is proven against a live engine
    /// in <c>ArgonComplexTest.MigrationLeaseTests</c>, but nothing there can notice a <em>second</em>
    /// lock quietly reappearing next to it. Two implementations of a distributed lock in one repository
    /// is the defect this change removed, and this is what keeps it removed.</para>
    ///
    /// <para><c>DELETE FROM</c> is listed on its own because it is the shape of the specific bug: a
    /// release with no owner predicate, in a <c>finally</c>, deleting whoever holds the lease now. The
    /// boot path has no business issuing a delete at all — the lease's release is the lease's.</para>
    /// </remarks>
    [Test]
    public void The_boot_path_carries_no_lock_of_its_own()
    {
        var literals = SqlLiteralsOf(File.ReadAllText(WarmUpSource().FullName));

        Assert.Multiple(() =>
        {
            // The positive control. Without it every assertion below would also pass against a file
            // whose literals this failed to extract, which is the way a scan like this goes quiet.
            Assert.That(literals, Does.Contain(BootLease),
                $"no literal named {BootLease}, so this scan is reading the wrong thing");

            Assert.That(literals, Does.Not.Contain(AbandonedLock),
                $"warm-up is writing SQL against {AbandonedLock} again; it should be holding a "
              + $"SchemaReconcileLease over {BootLease}");
            Assert.That(literals, Does.Not.Contain("DELETE FROM"),
                "a release on the boot path is the unqualified-delete defect coming back; the lease "
              + "releases itself, predicated on holder and fence");
            Assert.That(literals, Does.Not.Contain("expires_at"),
                "warm-up is managing a lease expiry itself again, which is where the client-clock "
              + "defect lived");
        });
    }

    #endregion
}
