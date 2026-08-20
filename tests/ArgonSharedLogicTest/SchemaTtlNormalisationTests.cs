namespace ArgonSharedLogicTest;

using Argon.Features.EF;

/// <summary>
/// That a declaration and the server's rendering of the same thing compare equal.
/// </summary>
/// <remarks>
/// <para>This is the piece the whole reconciler fails quietly without, and the failure is not a wrong
/// answer — it is a right answer computed twice a second forever. Without one canonicalising function
/// applied to <em>both</em> sides, a table declared <c>0 0 * * *</c> and running <c>@daily</c> shows a
/// difference on every boot, the reconciler emits a statement that changes nothing, and the next boot
/// emits it again. The metric would show permanent drift, the logs would show permanent activity, and
/// the database would be correct the entire time.</para>
///
/// <para>The other half is the parser. It reads <c>SHOW CREATE TABLE</c>, because row-level TTL is a
/// table storage parameter and storage parameters have no <c>information_schema</c> representation,
/// CockroachDB's <c>pg_class.reloptions</c> does not carry them, and <c>crdb_internal</c> is documented
/// as unstable and silently omits tables the caller cannot see. Every case here is a string, so none of
/// it needs a server.</para>
/// </remarks>
[TestFixture]
public class SchemaTtlNormalisationTests
{
    /// <summary>A CockroachDB rendering, with whatever storage-parameter clause is under test.</summary>
    /// <remarks>
    /// Written out in full rather than reduced to the clause alone, because the parser has to find that
    /// clause past a column list containing commas, parentheses, quoted mixed-case identifiers and
    /// string literals — which is exactly what it will be handed in production.
    /// </remarks>
    private static string CreateTable(string trailing = "")
        => $"""
            CREATE TABLE public."Invites" (
                id INT8 NOT NULL,
                "ExpireAt" TIMESTAMPTZ NOT NULL,
                note STRING NULL DEFAULT 'a, b (c)':::STRING,
                CONSTRAINT "Invites_pkey" PRIMARY KEY (id ASC)
            ){trailing}
            """;

    private static ObservedTtl Parse(string createStatement)
    {
        Assert.That(SchemaTtlCatalog.TryParse(createStatement, out var observed, out var failure), Is.True,
            $"the parser refused: {failure}");

        return observed;
    }

    #region cron

    /// <summary>The alias and the expression it stands for are one state, not two.</summary>
    [Test]
    public void A_named_cron_alias_and_its_expansion_are_the_same_schedule()
        => Assert.That(TtlSettings.CanonicalCron("@daily"), Is.EqualTo(TtlSettings.CanonicalCron("0 0 * * *")));

    [Test]
    public void Cron_whitespace_does_not_make_two_schedules()
        => Assert.That(TtlSettings.CanonicalCron("0   0 * *    *"), Is.EqualTo(TtlSettings.CanonicalCron("0 0 * * *")));

    /// <summary>
    /// An absent schedule is CockroachDB's default, not an unknown that matches anything.
    /// </summary>
    /// <remarks>
    /// The tempting alternative — treat a missing <c>ttl_job_cron</c> as "no information, assume it
    /// agrees" — is the <em>reporting converged when it could not look</em> failure in miniature. All
    /// three of Argon's TTL tables declare daily, so a server rendering no cron is running hourly and
    /// that is drift worth seeing.
    /// </remarks>
    [Test]
    public void An_absent_schedule_is_the_servers_hourly_default_and_therefore_differs_from_daily()
        => Assert.That(TtlSettings.CanonicalCron(null), Is.Not.EqualTo(TtlSettings.CanonicalCron("@daily")));

    /// <summary>Two crons that are not documented aliases of each other are left alone.</summary>
    /// <remarks>
    /// Deliberately not clever. Proving that two arbitrary cron expressions fire at the same instants is
    /// a different problem, and a reconciler that got it subtly wrong would suppress real drift rather
    /// than report a difference somebody can read.
    /// </remarks>
    [Test]
    public void An_unrecognised_cron_is_compared_as_text()
        => Assert.That(TtlSettings.CanonicalCron("0 0 */2 * *"), Is.EqualTo("0 0 */2 * *"));

    #endregion

    #region the expiration expression

    /// <summary>
    /// The generator quotes the column, the server keeps the quotes, and both sides mean the column.
    /// </summary>
    /// <remarks>
    /// The end-to-end property the reconciler needs: <c>WithTTL</c> stores <c>ExpireAt</c>, the
    /// generator writes <c>'"ExpireAt"'</c>, CockroachDB renders it back, and the two must land on one
    /// string. If they do not, the reconciler tries to "fix" an expiration expression that is already
    /// right — which is a <see cref="SchemaChangeTier.Approval"/> change to what gets deleted.
    /// </remarks>
    [Test]
    public void A_delimited_column_keeps_its_case_and_matches_the_declaration()
        => Assert.That(TtlSettings.CanonicalExpression("\"ExpireAt\""), Is.EqualTo("ExpireAt"));

    /// <summary>
    /// An undelimited one folds to lower, because that is what CockroachDB does to it.
    /// </summary>
    /// <remarks>
    /// Asymmetric on purpose. Folding both sides to one case would make a hand-written unquoted
    /// <c>ExpireAt</c> — which addresses a column called <c>expireat</c> that does not exist here —
    /// compare equal to the declaration and hide a real mistake.
    /// </remarks>
    [Test]
    public void An_undelimited_column_folds_to_lower_and_is_therefore_a_different_column()
        => Assert.That(TtlSettings.CanonicalExpression("ExpireAt"),
            Is.Not.EqualTo(TtlSettings.CanonicalExpression("\"ExpireAt\"")));

    [Test]
    public void An_embedded_quote_survives_the_round_trip()
        => Assert.That(TtlSettings.CanonicalExpression("\"od\"\"d\""), Is.EqualTo("od\"d"));

    #endregion

    #region absent and off

    /// <summary>No annotation and no TTL are the same state, and that is only true of TTL.</summary>
    /// <remarks>
    /// The locality half of this design cannot use this rule: CockroachDB renders a <c>LOCALITY</c>
    /// line for every table in a multi-region database, so a missing one means the reader failed. It
    /// renders no <c>ttl_</c> parameter at all for a table with no TTL, so a missing one means what it
    /// looks like. Sharing a rule between the two halves would be wrong in one of them.
    /// </remarks>
    [Test]
    public void A_table_with_no_ttl_clause_reports_the_same_state_as_no_declaration()
        => Assert.That(Parse(CreateTable()).Settings, Is.EqualTo(TtlSettings.Off));

    [Test]
    public void A_table_with_only_a_locality_clause_still_has_no_ttl()
        => Assert.That(Parse(CreateTable(" LOCALITY GLOBAL")).Settings, Is.EqualTo(TtlSettings.Off));

    /// <summary>
    /// Zero-valued batch knobs and unset ones are one state, on both sides.
    /// </summary>
    /// <remarks>
    /// <c>FriendRequestEntity</c> declares its TTL with every knob at <c>0</c>, and the generator omits
    /// a zero — so the database has never been told anything about batching for that table. This is the
    /// comparison that keeps it converged instead of permanently, unfixably drifting.
    /// </remarks>
    [Test]
    public void A_declared_zero_and_an_unset_server_default_are_the_same_state()
        => Assert.That(
            TtlSettings.Declared("RequestedAt", "@daily", 0, 0, 0),
            Is.EqualTo(TtlSettings.Observed("\"RequestedAt\"", "0 0 * * *", null, null, null)));

    #endregion

    #region the parser

    [Test]
    public void A_full_ttl_clause_reads_back_as_what_the_model_declared()
    {
        var observed = Parse(CreateTable(
            " WITH (ttl = 'on', ttl_expiration_expression = '\"ExpireAt\"', ttl_job_cron = '@daily', " +
            "ttl_select_batch_size = 5000, ttl_delete_batch_size = 5000, ttl_delete_rate_limit = 52428800)"));

        Assert.That(observed.Settings, Is.EqualTo(
            TtlSettings.Declared("ExpireAt", "0 0 * * *", 5000, 5000, 52428800)));
    }

    /// <summary>
    /// A doubled quote inside a value does not end it, and the parameters after it are still found.
    /// </summary>
    /// <remarks>
    /// Why the reader is a quote-aware scan rather than a regular expression. The values it steps over
    /// are SQL string literals that may contain commas, brackets and escaped quotes of their own — the
    /// column default in <see cref="CreateTable"/> carries all three — and a pattern that ended a value
    /// at the first <c>'</c> or split the clause at the first <c>,</c> would silently read half the
    /// parameters and report the rest as drift.
    /// </remarks>
    [Test]
    public void A_doubled_quote_inside_a_value_does_not_end_it_early()
    {
        var observed = Parse(CreateTable(
            " WITH (ttl = 'on', ttl_expiration_expression = '\"ExpireAt\" + INTERVAL ''1 day''', " +
            "ttl_job_cron = '@weekly')"));

        Assert.Multiple(() =>
        {
            Assert.That(observed.Settings.ExpirationExpression, Is.EqualTo("\"ExpireAt\" + INTERVAL '1 day'"));
            Assert.That(observed.Settings.JobCron, Is.EqualTo("0 0 * * 0"));
        });
    }

    /// <summary>CockroachDB annotates its own rendering with a type; the type is not part of the value.</summary>
    [Test]
    public void A_rendered_type_annotation_is_not_part_of_the_value()
        => Assert.That(Parse(CreateTable(" WITH (ttl = 'on', ttl_expire_after = '3 mons':::INTERVAL)")).ExpireAfter,
            Is.EqualTo("3 mons"));

    /// <summary>The operator's kill switch is read, so the planner can refuse to fight it.</summary>
    [Test]
    public void A_paused_ttl_is_read_as_paused()
        => Assert.That(
            Parse(CreateTable(" WITH (ttl = 'on', ttl_expiration_expression = '\"ExpireAt\"', ttl_pause = true)")).Paused,
            Is.True);

    /// <summary>Anything the model does not declare is kept to be reported, never quietly dropped.</summary>
    [Test]
    public void An_undeclared_ttl_parameter_is_kept_for_the_report()
        => Assert.That(
            Parse(CreateTable(" WITH (ttl = 'on', ttl_expiration_expression = '\"ExpireAt\"', ttl_label_metrics = true)"))
               .OtherParameters.Keys,
            Does.Contain("ttl_label_metrics"));

    /// <summary>
    /// A TTL that is on with nothing to expire on means the reader failed, and it says so.
    /// </summary>
    /// <remarks>
    /// The one place the parser refuses instead of guessing. CockroachDB does not render <c>ttl = 'on'</c>
    /// without an expiration source, so seeing one without the other means a value was lost — and a lost
    /// expression compared against a declared one looks exactly like drift the reconciler should close
    /// by rewriting a TTL that was already correct.
    /// </remarks>
    [Test]
    public void A_ttl_that_is_on_with_no_expiration_source_is_a_parse_failure_not_an_absence()
    {
        var read = SchemaTtlCatalog.TryParse(CreateTable(" WITH (ttl = 'on')"), out var observed, out var failure);

        Assert.Multiple(() =>
        {
            Assert.That(read, Is.False);
            Assert.That(observed, Is.EqualTo(ObservedTtl.Off));
            Assert.That(failure, Does.Contain("ttl_expiration_expression"));
        });
    }

    [Test]
    public void A_batch_size_that_is_not_a_number_is_a_parse_failure()
        => Assert.That(SchemaTtlCatalog.TryParse(
            CreateTable(" WITH (ttl_expiration_expression = '\"ExpireAt\"', ttl_select_batch_size = 'lots')"),
            out _, out _), Is.False);

    #endregion
}
