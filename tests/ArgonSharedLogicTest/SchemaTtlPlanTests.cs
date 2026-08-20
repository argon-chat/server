namespace ArgonSharedLogicTest;

using Argon.Features.EF;
using Microsoft.Extensions.Configuration;

/// <summary>
/// What the reconciler would run, and — much more of the time — what it refuses to.
/// </summary>
/// <remarks>
/// <para>Every case here is two records and a string, so the whole tier policy is asserted with no
/// database anywhere near it. That matters more than the convenience: the statements under test delete
/// rows, and the difference between "re-pace a delete job that is already running" and "start deleting
/// a year of accumulated rows" is one field on the observed side. It should be possible to change the
/// policy and find out in eleven seconds whether it still refuses what it must.</para>
///
/// <para>The one property that is not about any single case: <b>declared equals observed produces an
/// empty plan.</b> Idempotency is what makes a level-triggered reconciler safe to run on every boot of
/// every pod, and it is proven here rather than inferred from a passing integration run.</para>
/// </remarks>
[TestFixture]
public class SchemaTtlPlanTests
{
    private static readonly TableRef Invites = new("public", "Invites");

    /// <summary>What <c>SpaceInvite</c> actually declares, so the cases are the real ones.</summary>
    private static TtlSettings Declared(string cron = "0 0 * * *", int batch = 5000)
        => TtlSettings.Declared("ExpireAt", cron, batch, batch, 52428800);

    private static string CreateTable(string trailing = "")
        => $"""
            CREATE TABLE public."Invites" (
                id INT8 NOT NULL,
                "ExpireAt" TIMESTAMPTZ NOT NULL,
                CONSTRAINT "Invites_pkey" PRIMARY KEY (id ASC)
            ){trailing}
            """;

    /// <summary>The clause CockroachDB renders for a table carrying exactly what the model declares.</summary>
    private const string MatchingClause =
        " WITH (ttl = 'on', ttl_expiration_expression = '\"ExpireAt\"', ttl_job_cron = '@daily', " +
        "ttl_select_batch_size = 5000, ttl_delete_batch_size = 5000, ttl_delete_rate_limit = 52428800)";

    private static TtlObservation Observed(string trailing)
        => SchemaTtlCatalog.TryParse(CreateTable(trailing), out var observed, out var failure)
            ? TtlObservation.Read(observed)
            : TtlObservation.Unreadable(failure!);

    private static SchemaTtlItem Item(TtlSettings declared, TtlObservation observation)
        => SchemaTtlPlan
           .Build(new Dictionary<TableRef, TtlSettings> { [Invites] = declared },
                  new Dictionary<TableRef, TtlObservation> { [Invites] = observation })
           .Items.Single();

    private static SchemaTtlItem Item(string trailing) => Item(Declared(), Observed(trailing));

    #region convergence

    /// <summary>
    /// A server already carrying the declaration produces no statement at all.
    /// </summary>
    /// <remarks>
    /// The property every pod's boot depends on. If this ever goes red, the reconciler emits an
    /// <c>ALTER</c> on every boot of every pod forever, the drift metric never reaches zero, and the
    /// database is correct the whole time — a failure that presents as constant activity rather than as
    /// an error.
    /// </remarks>
    [Test]
    public void A_server_that_already_matches_produces_an_empty_plan()
    {
        var item = Item(MatchingClause);

        Assert.Multiple(() =>
        {
            Assert.That(item.Status, Is.EqualTo(SchemaTtlStatus.Converged));
            Assert.That(item.Statement, Is.Null);
        });
    }

    /// <summary>A table the migrations have not created yet is not drift.</summary>
    /// <remarks>
    /// Its <c>CREATE TABLE</c> carries the TTL clause — that path is the one the generator has always
    /// got right — so there is nothing to converge and nothing to warn about.
    /// </remarks>
    [Test]
    public void A_table_that_does_not_exist_yet_is_not_drift()
    {
        var plan = SchemaTtlPlan.Build(
            new Dictionary<TableRef, TtlSettings> { [Invites] = Declared() },
            new Dictionary<TableRef, TtlObservation> { [Invites] = TtlObservation.Missing });

        Assert.Multiple(() =>
        {
            Assert.That(plan.Items.Single().Status, Is.EqualTo(SchemaTtlStatus.Absent));
            Assert.That(plan.IsConverged, Is.True);
            Assert.That(plan.Items.Single().Statement, Is.Null);
        });
    }

    /// <summary>
    /// A table that could not be read is undetermined, and undetermined is never converged.
    /// </summary>
    /// <remarks>
    /// The failure this whole design singles out as the worst available, because it is the one that
    /// looks like success: a reconciler reporting that everything matches because it lacked the
    /// privilege to look. A permission error and "no TTL configured" must never collapse into one
    /// answer.
    /// </remarks>
    [Test]
    public void A_table_that_could_not_be_read_is_undetermined_and_never_converged()
    {
        var plan = SchemaTtlPlan.Build(
            new Dictionary<TableRef, TtlSettings> { [Invites] = Declared() },
            new Dictionary<TableRef, TtlObservation> { [Invites] = TtlObservation.Unreadable("42501") });

        Assert.Multiple(() =>
        {
            Assert.That(plan.HasUndetermined, Is.True);
            Assert.That(plan.IsConverged, Is.False);
            Assert.That(plan.Items.Single().Statement, Is.Null);
            Assert.That(plan.Runnable(SchemaChangeTier.Approval), Is.Empty);
        });
    }

    /// <summary>
    /// A parameter the model has no opinion about is a note, not something to reset.
    /// </summary>
    /// <remarks>
    /// <c>user_friend_requests</c> declares its batch knobs as <c>0</c>, which the generator omits, so
    /// the model is not asking for anything. A server value there is somebody's deliberate choice; the
    /// only statement that would "close" it is a <c>RESET</c> of a value nobody declared, which is
    /// converging downward. So it is reported and the table stays converged.
    /// </remarks>
    [Test]
    public void A_server_value_the_model_has_no_opinion_about_is_reported_not_reset()
    {
        var item = Item(TtlSettings.Declared("ExpireAt", "@daily", 0, 0, 0),
            Observed(" WITH (ttl = 'on', ttl_expiration_expression = '\"ExpireAt\"', ttl_job_cron = '@daily', " +
                     "ttl_select_batch_size = 700)"));

        Assert.Multiple(() =>
        {
            Assert.That(item.Status, Is.EqualTo(SchemaTtlStatus.Converged));
            Assert.That(item.Statement, Is.Null);
            Assert.That(item.Notes.Any(note => note.Contains("ttl_select_batch_size")), Is.True,
                "a server value the model does not declare has to be visible somewhere");
        });
    }

    #endregion

    #region what runs unattended, and what does not

    /// <summary>
    /// Re-pacing a TTL that is already deleting the right rows is the only thing a pod does by itself.
    /// </summary>
    /// <remarks>
    /// It cannot change which rows die — only how fast the job that was already deleting them gets
    /// through. And the statement carries only the parameter that differs, so the log line says what
    /// changed rather than restating the whole TTL.
    /// </remarks>
    [Test]
    public void A_schedule_that_differs_is_re_paced_automatically()
    {
        var item = Item(MatchingClause.Replace("'@daily'", "'@weekly'"));

        Assert.Multiple(() =>
        {
            Assert.That(item.Status, Is.EqualTo(SchemaTtlStatus.Drift));
            Assert.That(item.Tier, Is.EqualTo(SchemaChangeTier.Automatic));
            Assert.That(item.Statement, Does.Contain("ttl_job_cron = '0 0 * * *'"));
            Assert.That(item.Statement, Does.Not.Contain("ttl_expiration_expression"));
        });
    }

    /// <summary>
    /// Turning a TTL on is never automatic, because it schedules deletion of everything already expired.
    /// </summary>
    /// <remarks>
    /// The asymmetry the tiers exist for. On a table that has been accumulating since before anyone
    /// declared a TTL, the first job run deletes the entire backlog — and every silo role runs the
    /// warm-up path, so a hard reboot brings dozens of pods to this decision at the same instant. It
    /// belongs to somebody with a maintenance window.
    /// </remarks>
    [Test]
    public void Turning_a_ttl_on_needs_an_operator()
    {
        var item = Item("");

        Assert.Multiple(() =>
        {
            Assert.That(item.Tier, Is.EqualTo(SchemaChangeTier.Approval));
            Assert.That(item.Statement, Does.Contain("ttl_expiration_expression = '\"ExpireAt\"'"));
            Assert.That(item.Reason, Does.Contain("already past its expiration"));
        });
    }

    /// <summary>Changing which column decides expiry is the same act wearing a different hat.</summary>
    [Test]
    public void Changing_which_column_expires_rows_needs_an_operator()
        => Assert.That(
            Item(MatchingClause.Replace("'\"ExpireAt\"'", "'\"CreatedAt\"'")).Tier,
            Is.EqualTo(SchemaChangeTier.Approval));

    /// <summary>Nothing at any tier may run while an operator has the delete job paused.</summary>
    /// <remarks>
    /// <c>ttl_pause</c> is the documented lever for stopping a TTL job that is eating the cluster. A
    /// reconciler that reset the storage parameters around it — or cleared it outright — would restart
    /// the deletion a human stopped, during the incident they stopped it in. So a paused table is a
    /// hold, not a tier.
    /// </remarks>
    [Test]
    public void A_paused_delete_job_stops_everything_for_that_table()
    {
        var item = Item(MatchingClause.Replace("ttl = 'on'", "ttl = 'on', ttl_pause = true")
           .Replace("'@daily'", "'@weekly'"));

        Assert.Multiple(() =>
        {
            Assert.That(item.Tier, Is.EqualTo(SchemaChangeTier.Refused));
            Assert.That(item.Statement, Is.Null);
            Assert.That(item.Reason, Does.Contain("ttl_pause"));
        });
    }

    /// <summary>
    /// A table expiring rows the other way round is refused, because switching rewrites the table.
    /// </summary>
    /// <remarks>
    /// <c>ttl_expire_after</c> is backed by a hidden <c>crdb_internal_expiration</c> column, so moving
    /// to an expiration expression drops a column. That is a migration a human runs, not something a
    /// converger does on a boot.
    /// </remarks>
    [Test]
    public void A_table_using_expire_after_is_refused_rather_than_converted()
    {
        var item = Item(" WITH (ttl = 'on', ttl_expire_after = '3 mons':::INTERVAL, ttl_job_cron = '@daily')");

        Assert.Multiple(() =>
        {
            Assert.That(item.Tier, Is.EqualTo(SchemaChangeTier.Refused));
            Assert.That(item.Statement, Is.Null);
        });
    }

    /// <summary>The boot path can see an operator-tier change and still not be able to run it.</summary>
    [Test]
    public void The_boot_path_reports_an_operator_tier_change_without_running_it()
    {
        var plan = SchemaTtlPlan.Build(
            new Dictionary<TableRef, TtlSettings> { [Invites] = Declared() },
            new Dictionary<TableRef, TtlObservation> { [Invites] = Observed("") });

        Assert.Multiple(() =>
        {
            Assert.That(plan.Runnable(SchemaChangeTier.Automatic), Is.Empty);
            Assert.That(plan.Runnable(SchemaChangeTier.Approval).Count(), Is.EqualTo(1));
            Assert.That(plan.Blocked(SchemaChangeTier.Automatic).Count(), Is.EqualTo(1));
        });
    }

    #endregion

    #region statement text and ordering

    /// <summary>
    /// Identifiers are delimited, because Argon's are mixed case and an unquoted one folds to lower.
    /// </summary>
    /// <remarks>
    /// An <c>ALTER TABLE public.invites</c> does not fail in a way anybody notices from the outside —
    /// it fails with "relation does not exist", which the reader reports as a table that has not been
    /// created yet.
    /// </remarks>
    [Test]
    public void The_statement_delimits_a_mixed_case_table_name()
        => Assert.That(Item("").Statement, Does.StartWith("ALTER TABLE \"public\".\"Invites\" SET ("));

    /// <summary>Cheapest first, then by name, so two pods compute the same order.</summary>
    /// <remarks>
    /// A pass that is interrupted has then done the least alarming things available to it, and two
    /// deploys' logs can be diffed against each other.
    /// </remarks>
    [Test]
    public void The_plan_runs_the_cheapest_change_first_and_is_otherwise_ordered_by_name()
    {
        var zebra = new TableRef("public", "Zebra");
        var alpha = new TableRef("public", "Alpha");

        var plan = SchemaTtlPlan.Build(
            new Dictionary<TableRef, TtlSettings>
            {
                [alpha]   = Declared(),
                [zebra]   = Declared(),
                [Invites] = Declared()
            },
            new Dictionary<TableRef, TtlObservation>
            {
                // Both need an operator; the one that only needs re-pacing must come before them.
                [alpha]   = Observed(""),
                [zebra]   = Observed(""),
                [Invites] = Observed(MatchingClause.Replace("'@daily'", "'@weekly'"))
            });

        Assert.That(plan.Items.Select(item => item.Table.Name),
            Is.EqualTo(new[] { "Invites", "Alpha", "Zebra" }).AsCollection);
    }

    #endregion

    #region the default posture

    /// <summary>
    /// Shipped in report mode, and a typo in a config map does not turn that into apply.
    /// </summary>
    /// <remarks>
    /// The direction of this default is the opposite of <c>Database:Provider</c>'s on purpose. That key
    /// fails open towards CockroachDB because all it selects is a SQL generator; this one decides
    /// whether a pod issues DDL against production while it boots.
    /// </remarks>
    [TestCase((string?)null, TestName = "Mode_unset_reports_rather_than_applies")]
    [TestCase("", TestName = "Mode_empty_reports_rather_than_applies")]
    [TestCase("apply-please", TestName = "Mode_misspelled_reports_rather_than_applies")]
    public void An_unreadable_mode_reports_rather_than_applies(string? configured)
        => Assert.That(Mode(configured), Is.EqualTo(SchemaReconcileMode.Report));

    [Test]
    public void Apply_has_to_be_asked_for_by_name()
        => Assert.That(Mode("Apply"), Is.EqualTo(SchemaReconcileMode.Apply));

    private static SchemaReconcileMode Mode(string? configured)
        => SchemaReconcileOptions.FromConfiguration(new ConfigurationBuilder()
           .AddInMemoryCollection([new KeyValuePair<string, string?>(SchemaReconcileOptions.ModeKey, configured)])
           .Build()).Mode;

    #endregion
}
