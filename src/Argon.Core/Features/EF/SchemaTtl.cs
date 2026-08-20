namespace Argon.Features.EF;

using System.Text.RegularExpressions;

/// <summary>
/// One table's row-level TTL, reduced to the single shape both sides of the comparison are read into.
/// </summary>
/// <remarks>
/// <para>The whole reconciler rests on this type having exactly one canonical form per physical state.
/// Three declarations that mean the same thing arrive by different routes — the model says
/// <c>batchSize: 0</c> for "leave the server's default alone", the server says nothing at all for the
/// same thing, and a cron of <c>0 0 * * *</c> comes back as <c>@daily</c> or the other way round — and
/// if any of those survives into the comparison then every table drifts on every boot and the
/// reconciler emits statements forever that change nothing. That is the failure this record exists to
/// make impossible, which is why nothing constructs one directly: both entry points are factories that
/// canonicalise, and the setters are private so a half-canonical instance cannot be built.</para>
///
/// <para>Only the parameters that are compared live here. What the server reports and the model has no
/// opinion about — <c>ttl_pause</c>, <c>ttl_expire_after</c>, anything else beginning <c>ttl_</c> —
/// lives on <see cref="ObservedTtl"/> instead, deliberately outside record equality: those are things
/// to report, never things to converge, and putting them in the comparison would turn "an operator
/// paused the delete job during an incident" into drift the reconciler wants to undo.</para>
/// </remarks>
public sealed record TtlSettings
{
    /// <summary>No row-level TTL. What a table with no annotation declares, and what a table with no TTL reports.</summary>
    /// <remarks>
    /// Absence and off really are the same state here, and that is not true of the locality half of the
    /// design: CockroachDB renders a <c>LOCALITY</c> line for every table in a multi-region database, so
    /// a missing one means the reader failed rather than that the table is unplaced. It renders no
    /// <c>ttl_</c> parameter at all for a table with no TTL, so a missing one means exactly what it
    /// looks like. The two halves must not be given the same rule.
    /// </remarks>
    public static readonly TtlSettings Off = new();

    /// <summary>CockroachDB's own default when a TTL is enabled without naming a schedule.</summary>
    /// <remarks>
    /// Load-bearing, and the one constant here that is a claim about the server rather than about
    /// Argon. A descriptor carrying the default may render no <c>ttl_job_cron</c> at all, and reading
    /// that as "unknown, assume it matches" is the <em>reporting converged when it could not look</em>
    /// failure. So absence resolves to this; if Argon declares anything else — and all three of its TTL
    /// tables declare daily — the difference surfaces as the real drift it is.
    /// </remarks>
    public const string DefaultJobCron = "0 * * * *";

    public bool Enabled { get; private init; }

    /// <summary>
    /// The <c>ttl_expiration_expression</c>, canonicalised to the column identifier it names.
    /// </summary>
    /// <remarks>
    /// A column name rather than arbitrary SQL because a column name is all <c>WithTTL</c> can produce:
    /// it resolves the property to a column and the generator delimits it. Anything else the server
    /// reports is kept verbatim and compared as text — proving that two different SQL expressions
    /// select the same rows is not something this can do, and guessing would be worse than reporting a
    /// difference a human can read.
    /// </remarks>
    public string? ExpirationExpression { get; private init; }

    public string JobCron { get; private init; } = DefaultJobCron;

    /// <summary>
    /// <c>null</c> means "the server's default", which is what both an unset parameter and the model's
    /// <c>0</c> mean.
    /// </summary>
    /// <remarks>
    /// The zero mapping is not cosmetic. <c>FriendRequestEntity</c> declares a TTL with every batch knob
    /// left at <c>0</c>, and <c>MultiregionalMigrationsSqlGenerator</c> skips a parameter whose value is
    /// zero — so a zero has never reached a database and never will. Comparing it as the number zero
    /// would make that table drift against a server that is doing precisely what was asked of it.
    /// </remarks>
    public int? SelectBatchSize { get; private init; }

    public int? DeleteBatchSize { get; private init; }

    public int? DeleteRateLimit { get; private init; }

    /// <summary>
    /// What the model declares, or <see cref="Off"/> when it declares nothing.
    /// </summary>
    /// <param name="expirationColumn">
    /// The column name as <c>WithTTL</c> resolved it, <em>not</em> a SQL fragment: the generator quotes
    /// it on the way out, so the server preserves its case and the canonical form is the name itself.
    /// Pre-quoted text passed here would canonicalise to a name with quotes inside it and match nothing.
    /// </param>
    /// <remarks>
    /// <c>ttl_range_concurrency</c> is accepted by <c>WithTTL</c> and is deliberately absent from this
    /// record. CockroachDB made it a no-op — it is not stored on the descriptor and does not come back
    /// out of <c>SHOW CREATE TABLE</c> — so comparing it would make two of the three TTL tables drift on
    /// every boot forever against a server that cannot possibly satisfy them. It stays in the annotation
    /// and in the <c>CREATE TABLE</c> clause, where it is inert and harmless; it is reported as inert
    /// rather than silently dropped, so nobody concludes from the plan that it took effect.
    /// </remarks>
    public static TtlSettings Declared(
        string expirationColumn,
        string? jobCron,
        int selectBatchSize,
        int deleteBatchSize,
        int deleteRateLimit)
        => new()
        {
            Enabled              = true,
            ExpirationExpression = expirationColumn,
            JobCron              = CanonicalCron(jobCron),
            SelectBatchSize      = ZeroMeansUnset(selectBatchSize),
            DeleteBatchSize      = ZeroMeansUnset(deleteBatchSize),
            DeleteRateLimit      = ZeroMeansUnset(deleteRateLimit)
        };

    /// <summary>What the server reports, read out of its own storage parameters.</summary>
    public static TtlSettings Observed(
        string? expirationExpressionSql,
        string? jobCron,
        int? selectBatchSize,
        int? deleteBatchSize,
        int? deleteRateLimit)
        => new()
        {
            Enabled              = true,
            ExpirationExpression = CanonicalExpression(expirationExpressionSql),
            JobCron              = CanonicalCron(jobCron),
            SelectBatchSize      = selectBatchSize,
            DeleteBatchSize      = deleteBatchSize,
            DeleteRateLimit      = deleteRateLimit
        };

    private static int? ZeroMeansUnset(int value) => value == 0 ? null : value;

    /// <summary>
    /// The named cron aliases, folded so a declaration and the server's rendering of it agree.
    /// </summary>
    /// <remarks>
    /// Only the documented aliases are folded. Proving that two arbitrary cron expressions fire at the
    /// same instants is a different and much larger problem, and a reconciler that got it subtly wrong
    /// would suppress real drift — so anything outside this table is compared as text, and a difference
    /// is reported rather than reasoned away.
    /// </remarks>
    private static readonly Dictionary<string, string> CronAliases = new(StringComparer.OrdinalIgnoreCase)
    {
        ["@hourly"]   = "0 * * * *",
        ["@daily"]    = "0 0 * * *",
        ["@midnight"] = "0 0 * * *",
        ["@weekly"]   = "0 0 * * 0",
        ["@monthly"]  = "0 0 1 * *",
        ["@yearly"]   = "0 0 1 1 *",
        ["@annually"] = "0 0 1 1 *"
    };

    private static readonly Regex Whitespace = new(@"\s+", RegexOptions.Compiled);

    /// <summary>An absent schedule is the server's default, not an unknown.</summary>
    public static string CanonicalCron(string? cron)
    {
        if (string.IsNullOrWhiteSpace(cron))
            return DefaultJobCron;

        var collapsed = Whitespace.Replace(cron.Trim(), " ");

        return CronAliases.TryGetValue(collapsed, out var expanded) ? expanded : collapsed;
    }

    private static readonly Regex QuotedIdentifier = new("^\"(?:[^\"]|\"\")*\"$", RegexOptions.Compiled);
    private static readonly Regex BareIdentifier   = new(@"^[A-Za-z_][A-Za-z0-9_$]*$", RegexOptions.Compiled);

    /// <summary>
    /// A <c>ttl_expiration_expression</c> as the server rendered it, reduced to the column it names.
    /// </summary>
    /// <remarks>
    /// The case rule is asymmetric on purpose, because CockroachDB's is: a delimited identifier keeps
    /// its case, a bare one folds to lower. Argon's columns are mixed case and the generator always
    /// delimits them, so <c>"ExpireAt"</c> canonicalises to <c>ExpireAt</c> and matches the declaration
    /// — while a hand-written unquoted <c>ExpireAt</c> canonicalises to <c>expireat</c>, which really is
    /// a different column, and surfaces as the drift it is. Folding both to one case would hide exactly
    /// that mistake.
    /// </remarks>
    public static string? CanonicalExpression(string? expression)
    {
        if (string.IsNullOrWhiteSpace(expression))
            return null;

        var trimmed = expression.Trim();

        if (QuotedIdentifier.IsMatch(trimmed))
            return trimmed[1..^1].Replace("\"\"", "\"");

        if (BareIdentifier.IsMatch(trimmed))
            return trimmed.ToLowerInvariant();

        return Whitespace.Replace(trimmed, " ");
    }
}

/// <summary>
/// Everything the server says about a table's TTL — the comparable part, and the part that is only
/// ever reported.
/// </summary>
/// <param name="Settings">The canonical state, for comparison against the declaration.</param>
/// <param name="Paused">
/// <c>ttl_pause</c>. The operator's kill switch for a delete job that is eating the cluster, and
/// therefore a hold: while it is set, this table gets no statement in any mode. A converger that
/// cleared it — or that reset the storage parameters around it — would restart the deletion a human
/// stopped, during the incident they stopped it in.
/// </param>
/// <param name="ExpireAfter">
/// <c>ttl_expire_after</c>. Argon never declares it, and it is not interchangeable with an expiration
/// expression: it is backed by a hidden <c>crdb_internal_expiration</c> column, so moving between the
/// two adds or drops a column and rewrites the table. Reported, never converged.
/// </param>
/// <param name="OtherParameters">
/// Any other <c>ttl_</c> parameter the server carries. Kept so the report can name it; never removed,
/// because a parameter the model does not mention is not evidence that nobody wanted it.
/// </param>
public sealed record ObservedTtl(
    TtlSettings Settings,
    bool Paused,
    string? ExpireAfter,
    IReadOnlyDictionary<string, string> OtherParameters)
{
    public static ObservedTtl Off { get; } =
        new(TtlSettings.Off, Paused: false, ExpireAfter: null, new Dictionary<string, string>());
}

/// <summary>Which table, in which schema. Compared the way CockroachDB compares identifiers: exactly.</summary>
/// <remarks>
/// A record rather than a tuple so the dictionaries that key on it read as what they are, and so the
/// error messages the desired-state reader raises can print one thing rather than assembling it at
/// three call sites.
/// </remarks>
public readonly record struct TableRef(string Schema, string Name)
{
    public const string DefaultSchema = "public";

    public override string ToString() => $"{Schema}.{Name}";

    /// <summary>The name as it goes into a statement: delimited, because Argon's are mixed case.</summary>
    /// <remarks>
    /// An unquoted mixed-case identifier folds to lower and addresses a table that does not exist —
    /// <c>TablePlacementTests</c> already depends on this, and a reconciler that got it wrong would
    /// report a missing table rather than a wrong one, which reads like good news.
    /// </remarks>
    public string Quoted => $"{Delimit(Schema)}.{Delimit(Name)}";

    public static string Delimit(string identifier) => $"\"{identifier.Replace("\"", "\"\"")}\"";
}
