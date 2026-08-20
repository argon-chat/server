namespace Argon.Features.EF;

using System.Text.RegularExpressions;

/// <summary>
/// One table's declared row-level TTL, in the single shape everything downstream reads.
/// </summary>
/// <remarks>
/// <para>Two consumers, one declaration: <see cref="SchemaDeclarations"/> renders it into the
/// CockroachDB storage parameters, and <see cref="TtlSweepTargets"/> turns it into the PostgreSQL
/// sweep that stands in for them. That is the whole reason the annotation is parsed once into a type
/// rather than read twice — one <c>WithTTL</c> call has to mean the same thing on both engines, and
/// two readers of one payload disagreeing about its shape is the class of bug this exists to prevent.</para>
///
/// <para>Nothing constructs one directly: <see cref="Declared"/> is the only entry point and the
/// setters are private, so a value that skipped the normalisation cannot exist. The normalisation that
/// remains is about what a <em>declaration</em> can leave unsaid — <c>WithTTL</c>'s <c>0</c> defaults
/// and a named cron alias. The machinery that once folded the <em>server's</em> rendering into the
/// same shape went with the reconciler that read the server back; nothing reads it now.</para>
/// </remarks>
public sealed record TtlSettings
{
    /// <summary>No row-level TTL. What a table with no annotation declares.</summary>
    /// <remarks>
    /// Never produced by <see cref="SchemaTtlModel.ReadDesiredState"/> — a table with no annotation is
    /// absent from that dictionary rather than present with this value. It is the type's zero, kept so
    /// that a consumer handed a <see cref="TtlSettings"/> from anywhere can ask
    /// <see cref="Enabled"/> instead of assuming.
    /// </remarks>
    public static readonly TtlSettings Off = new();

    /// <summary>CockroachDB's own default when a TTL is enabled without naming a schedule.</summary>
    /// <remarks>
    /// A declaration that names no cron means "whatever the server does", and that is this. Spelled out
    /// rather than left null so the sweeper and the <c>ALTER</c> agree on what an unstated schedule is.
    /// </remarks>
    public const string DefaultJobCron = "0 * * * *";

    public bool Enabled { get; private init; }

    /// <summary>
    /// The <c>ttl_expiration_expression</c>, as the column identifier <c>WithTTL</c> resolved.
    /// </summary>
    /// <remarks>
    /// A bare column name, undelimited: <c>WithTTL</c> resolves the property to a column and the
    /// consumers quote it on the way into SQL. Pre-quoted text stored here would come back out with
    /// quotes inside quotes and address nothing.
    /// </remarks>
    public string? ExpirationExpression { get; private init; }

    public string JobCron { get; private init; } = DefaultJobCron;

    /// <summary>
    /// <c>null</c> means "the server's default", which is what the model's <c>0</c> means.
    /// </summary>
    /// <remarks>
    /// The zero mapping is not cosmetic. <c>FriendRequestEntity</c> declares a TTL with every batch knob
    /// left at <c>0</c>, and <c>MultiregionalMigrationsSqlGenerator</c> skips a parameter whose value is
    /// zero — so a zero has never reached a database and never should. Emitting it as the number zero
    /// would switch off pacing the server was doing correctly.
    /// </remarks>
    public int? SelectBatchSize { get; private init; }

    public int? DeleteBatchSize { get; private init; }

    public int? DeleteRateLimit { get; private init; }

    /// <summary>The declaration one <c>WithTTL</c> annotation stands for.</summary>
    /// <param name="expirationColumn">
    /// The column name as <c>WithTTL</c> resolved it, <em>not</em> a SQL fragment — see
    /// <see cref="ExpirationExpression"/>.
    /// </param>
    /// <remarks>
    /// <c>ttl_range_concurrency</c> is accepted by <c>WithTTL</c> and is deliberately absent from this
    /// record. CockroachDB made it a no-op — it is not stored on the descriptor — so carrying it here
    /// would only give two of the three TTL tables a parameter the server cannot satisfy. It stays in
    /// the annotation and in the <c>CREATE TABLE</c> clause, where it is inert and harmless.
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

    private static int? ZeroMeansUnset(int value) => value == 0 ? null : value;

    /// <summary>
    /// The named cron aliases, expanded so that a declaration written either way means one thing.
    /// </summary>
    /// <remarks>
    /// Only the documented aliases are folded. Proving that two arbitrary cron expressions fire at the
    /// same instants is a different and much larger problem, so anything outside this table is carried
    /// through as written.
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
    private static string CanonicalCron(string? cron)
    {
        if (string.IsNullOrWhiteSpace(cron))
            return DefaultJobCron;

        var collapsed = Whitespace.Replace(cron.Trim(), " ");

        return CronAliases.TryGetValue(collapsed, out var expanded) ? expanded : collapsed;
    }
}

/// <summary>Which table, in which schema. Compared the way CockroachDB compares identifiers: exactly.</summary>
/// <remarks>
/// A record rather than a tuple so the dictionaries that key on it read as what they are, and so the
/// error messages the declaration readers raise can print one thing rather than assembling it at
/// three call sites.
/// </remarks>
public readonly record struct TableRef(string Schema, string Name)
{
    public const string DefaultSchema = "public";

    public override string ToString() => $"{Schema}.{Name}";

    /// <summary>The name as it goes into a statement: delimited, because Argon's are mixed case.</summary>
    /// <remarks>
    /// An unquoted mixed-case identifier folds to lower and addresses a table that does not exist —
    /// <c>TablePlacementTests</c> already depends on this, and a statement that got it wrong would come
    /// back as a missing table rather than a wrong one, which reads like good news.
    /// </remarks>
    public string Quoted => $"{Delimit(Schema)}.{Delimit(Name)}";

    public static string Delimit(string identifier) => $"\"{identifier.Replace("\"", "\"\"")}\"";
}
