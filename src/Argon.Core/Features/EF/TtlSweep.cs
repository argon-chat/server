namespace Argon.Features.EF;

using Microsoft.EntityFrameworkCore.Metadata;
using System.Text.RegularExpressions;

/// <summary>How much the sweeper is allowed to do.</summary>
/// <remarks>
/// Deliberately the same three-way shape as <see cref="SchemaReconcileMode"/>, and for the same
/// reason: the interesting question is never "is it on" but "is it allowed to change anything". The
/// difference is that the reconciler's worst statement re-paces a delete job, while this one's deletes
/// rows outright and no <c>ALTER</c> puts them back.
/// </remarks>
public enum TtlSweepMode
{
    /// <summary>Nothing at all — not even the count. The kill switch.</summary>
    Off,

    /// <summary>
    /// Count what would be deleted, delete nothing. <b>The default.</b>
    /// </summary>
    /// <remarks>
    /// The default is report and it must stay report. These three tables have never been swept by
    /// anything, on either engine, so the first pass on a long-lived database is a backlog pass —
    /// exactly the act <see cref="SchemaChangeTier.Approval"/> exists to keep off the boot path over on
    /// the reconciler side. A number in a log line costs a scan; a wrong predicate applied
    /// automatically costs the table.
    /// </remarks>
    Report,

    /// <summary>Actually delete, in batches, up to the per-table budget.</summary>
    Apply
}

/// <summary>Everything the sweeper reads from configuration.</summary>
/// <param name="Interval">
/// How often the reminder fires. Not the declared <c>ttl_job_cron</c> — see
/// <see cref="TtlSweepTarget"/> for why the cron is reported and not honoured.
/// </param>
/// <param name="DefaultBatchSize">
/// Rows per statement for a table whose annotation declares no batch size, which is
/// <c>user_friend_requests</c> — <c>WithTTL</c>'s batch arguments default to <c>0</c> and zero means
/// "no opinion", never "a batch of zero".
/// </param>
/// <param name="RowBudgetPerTable">
/// The most rows one pass may delete from one table. A ceiling on blast radius per pass rather than a
/// limit on what eventually gets deleted: a table over budget is swept again on the next tick, and the
/// pass says so.
/// </param>
/// <param name="MinimumBatchDelay">
/// The floor on the pause between batches, and the only rate limit that is actually load-bearing here.
/// <c>ttl_delete_rate_limit</c> is documented in <em>rows per second</em>, and Argon's two invite
/// tables declare <c>52428800</c> — which is 50 MiB, a byte count in a knob that does not take one.
/// As a rate limit it means 52 million rows a second, i.e. none at all. So the derived delay is taken
/// only when it is larger than this floor, and the floor is what keeps a sweep from being a tight loop
/// against the primary.
/// </param>
public sealed record TtlSweepOptions(
    TtlSweepMode Mode,
    TimeSpan Interval,
    int DefaultBatchSize,
    int RowBudgetPerTable,
    TimeSpan MinimumBatchDelay,
    TimeSpan LeaseLifetime)
{
    public const string ModeKey              = "Database:TtlSweep:Mode";
    public const string IntervalKey          = "Database:TtlSweep:Interval";
    public const string DefaultBatchSizeKey  = "Database:TtlSweep:DefaultBatchSize";
    public const string RowBudgetKey         = "Database:TtlSweep:RowBudgetPerTable";
    public const string MinimumBatchDelayKey = "Database:TtlSweep:MinimumBatchDelay";

    /// <summary>Orleans refuses a reminder period below one minute, so neither does this.</summary>
    public static readonly TimeSpan MinimumInterval = TimeSpan.FromMinutes(1);

    /// <summary>
    /// What every deployment gets unless it says otherwise.
    /// </summary>
    /// <remarks>
    /// <para>An hour rather than the declared daily cron, on purpose. A daily pass on a table that has
    /// been accumulating for a year has a year of rows to get through in one sitting; an hourly pass
    /// has an hour's worth, which is the difference between a spike and a background hum. The
    /// user-visible consequence of sweeping more often than CockroachDB does is that an expired invite
    /// stops answering <c>EXPIRED</c> and starts answering <c>NOT_FOUND</c> sooner — both engines
    /// already pass through both states, and neither the length of that window nor which of the two a
    /// caller sees is a contract anything depends on.</para>
    ///
    /// <para><c>500</c> is CockroachDB's own documented <c>ttl_select_batch_size</c> default. The
    /// select default rather than the delete one because a single statement here does both halves.</para>
    /// </remarks>
    public static readonly TtlSweepOptions Default = new(
        TtlSweepMode.Report,
        TimeSpan.FromHours(1),
        DefaultBatchSize: 500,
        RowBudgetPerTable: 10_000,
        MinimumBatchDelay: TimeSpan.FromMilliseconds(100),
        LeaseLifetime: TimeSpan.FromMinutes(2));

    /// <summary>
    /// Reads configuration, defaulting to <see cref="TtlSweepMode.Report"/>.
    /// </summary>
    /// <remarks>
    /// Unset and unparsable both land on <c>Report</c>, the same direction
    /// <see cref="SchemaReconcileOptions.FromConfiguration"/> chose and for a stronger reason: a typo
    /// in a config map must never be the thing that starts deleting rows.
    /// </remarks>
    public static TtlSweepOptions FromConfiguration(IConfiguration configuration)
        => Default with
        {
            Mode = Enum.TryParse<TtlSweepMode>(configuration[ModeKey], ignoreCase: true, out var mode)
                ? mode
                : TtlSweepMode.Report,
            Interval = TimeSpan.TryParse(configuration[IntervalKey], out var interval) && interval >= MinimumInterval
                ? interval
                : Default.Interval,
            DefaultBatchSize = int.TryParse(configuration[DefaultBatchSizeKey], out var batch) && batch > 0
                ? batch
                : Default.DefaultBatchSize,
            RowBudgetPerTable = int.TryParse(configuration[RowBudgetKey], out var budget) && budget > 0
                ? budget
                : Default.RowBudgetPerTable,
            MinimumBatchDelay = TimeSpan.TryParse(configuration[MinimumBatchDelayKey], out var delay) && delay >= TimeSpan.Zero
                ? delay
                : Default.MinimumBatchDelay
        };
}

/// <summary>
/// One table, reduced to the statements that would sweep it — or to the reason nothing will.
/// </summary>
/// <remarks>
/// <para><b>The predicate is <c>column &lt; now()</c> and nothing else, because that is all the
/// annotation can mean.</b> CockroachDB's <c>ttl_expiration_expression</c> deletes a row once the
/// expression is in the past, and <c>WithTTL</c> can only ever produce a bare column name — it resolves
/// a property to its column and the generator delimits it. So the declared column <em>is</em> the
/// instant the row expires. There is no retention interval anywhere in
/// <see cref="ExpirationJobAnnotation"/>, which means "delete rows older than N days" is not
/// expressible: a table that wants that has to carry a column holding the deadline, which is precisely
/// what <c>AsTTlField</c> exists for.</para>
///
/// <para><b>The declared cron is reported, not honoured.</b> There is no cron evaluator in this
/// repository and this is not the place to introduce one — a subtly wrong parser would move when rows
/// die, silently. What CockroachDB's cron actually buys is an upper bound on how long an expired row
/// survives, and <see cref="TtlSweepOptions.Interval"/> gives the same bound by a shorter route. A
/// declaration whose cron is not daily is worth a note in the log, and gets one; it is not worth a
/// dependency.</para>
///
/// <para><b><c>ctid</c>, and therefore PostgreSQL only.</b> The three TTL tables have three different
/// primary keys — one <c>INT8</c>, two composites — and batching a delete by key means generating a
/// different statement shape per table from model metadata. <c>ctid</c> is one shape for all of them,
/// and it is safe here specifically because <c>FOR UPDATE</c> pins the tuples between the sub-select
/// and the delete: without the lock a concurrent update moves a row to a new <c>ctid</c> and the
/// delete misses it. <c>SKIP LOCKED</c> is what stops the sweep from queueing behind a member
/// accepting the very invite it is trying to remove — that row simply goes in the next batch.</para>
///
/// <para>A class rather than a record, unlike everything around it. A record's synthesized
/// <c>ToString</c> prints every readable public property, and three of these are computed properties
/// that throw on a refused target — so a log line or an assertion message holding one would blow up
/// precisely when something had already gone wrong. Nothing needs value equality here.</para>
/// </remarks>
public sealed class TtlSweepTarget
{
    public required TableRef Table { get; init; }

    /// <summary>The expiration column, canonical and undelimited. <c>null</c> on a refused target.</summary>
    public string? ExpirationColumn { get; init; }

    public int      BatchSize  { get; init; }
    public TimeSpan BatchDelay { get; init; }
    public int      RowBudget  { get; init; }

    /// <summary>What the model declared, carried so the log can print it. Never used to schedule.</summary>
    public string? DeclaredCron { get; init; }

    /// <summary>Why this table will not be swept, or <c>null</c> when it will be.</summary>
    public string? Refusal { get; init; }

    public bool IsSweepable => Refusal is null;

    /// <summary>
    /// The rows this target claims. Built from a validated identifier, never from free text.
    /// </summary>
    /// <remarks>
    /// <c>&lt;</c> rather than <c>&lt;=</c> to match CockroachDB, and <c>now()</c> rather than a
    /// client clock: the deadline in the row and the moment it is compared against must come from the
    /// same machine, or a pod with a fast clock deletes rows that have not expired anywhere else.
    /// </remarks>
    public string Predicate
    {
        get
        {
            if (ExpirationColumn is not { Length: > 0 } column)
                throw new InvalidOperationException(
                    $"{Table} was refused ({Refusal}); a refused target has no predicate. " +
                    "Check IsSweepable before asking for one.");

            return $"{TableRef.Delimit(column)} < now()";
        }
    }

    /// <summary>How many rows currently match, counted no further than one over the budget.</summary>
    /// <remarks>
    /// Bounded because an unbounded <c>count(*)</c> on a table nobody has ever swept is a sequential
    /// scan of the whole backlog, on a schedule, for a number the report only needs to be able to say
    /// "at least" about. The report distinguishes an exact count from a capped one.
    /// </remarks>
    public string CountSql
        => $"""
            SELECT count(*) FROM (
                SELECT 1 FROM {Table.Quoted} WHERE {Predicate} LIMIT {RowBudget + 1}
            ) AS expired
            """;

    /// <summary>One batch. Issued repeatedly until it deletes nothing or the budget runs out.</summary>
    public string DeleteBatchSql
        => $"""
            DELETE FROM {Table.Quoted}
             WHERE ctid IN (
                 SELECT ctid FROM {Table.Quoted}
                  WHERE {Predicate}
                  LIMIT {BatchSize}
                  FOR UPDATE SKIP LOCKED
             )
            """;

    public static TtlSweepTarget Refused(TableRef table, string reason)
        => new()
        {
            Table   = table,
            Refusal = reason
        };
}

/// <summary>
/// Turns the model's TTL declarations into sweep targets — the same declarations, read the same way.
/// </summary>
/// <remarks>
/// <para>Desired state comes from <see cref="SchemaTtlModel.ReadDesiredState"/> and from nowhere else,
/// which is the whole point of the exercise: one <c>WithTTL</c> call is what makes CockroachDB delete
/// a row and what makes this delete the same row, so the two engines cannot drift apart by someone
/// updating one list and not the other. Everything downstream of that read is Postgres-shaped; the read
/// itself is engine-independent.</para>
/// </remarks>
public static class TtlSweepTargets
{
    /// <summary>
    /// The only shape a declared expiration column may have before it is interpolated into SQL.
    /// </summary>
    /// <remarks>
    /// The column name reaches this from the EF model, so it is developer-controlled and not user
    /// input — and it is checked anyway, because the alternative is a file that builds <c>DELETE</c>
    /// statements by string concatenation with no stated invariant about what goes into them. The
    /// check is also load-bearing for a second reason: <see cref="TtlSettings.ExpirationExpression"/>
    /// can hold arbitrary SQL when it was read off a server, and a future caller handing this an
    /// observed value rather than a declared one must fail closed rather than build a predicate out of
    /// somebody's expression.
    /// </remarks>
    private static readonly Regex PlainIdentifier = new(@"^[A-Za-z_][A-Za-z0-9_$]*$", RegexOptions.Compiled);

    /// <summary>The SQL defaults that mean "the moment this row was written".</summary>
    /// <remarks>
    /// Spelled out rather than pattern-matched on the substring "now", so that a default of
    /// <c>'2999-01-01'</c> or a call into an application function is not swept up by accident. Compared
    /// with whitespace removed and case folded, because a default comes back from the model exactly as
    /// it was written.
    /// </remarks>
    private static readonly HashSet<string> CurrentTimeDefaults = new(StringComparer.OrdinalIgnoreCase)
    {
        "now()",
        "current_timestamp",
        "current_timestamp()",
        "localtimestamp",
        "transaction_timestamp()",
        "statement_timestamp()",
        "clock_timestamp()"
    };

    private static readonly Regex Whitespace = new(@"\s+", RegexOptions.Compiled);

    /// <summary>Every declared table, as something that can be swept or as something that will not be.</summary>
    /// <remarks>
    /// Refused targets are returned rather than filtered out. A table that is silently absent from the
    /// list is indistinguishable from a table nobody declared, and the one table this currently refuses
    /// is refused because of a defect somebody needs to see.
    /// </remarks>
    public static IReadOnlyList<TtlSweepTarget> Resolve(IModel model, TtlSweepOptions options)
        => SchemaTtlModel.ReadDesiredState(model)
           .OrderBy(pair => pair.Key.ToString(), StringComparer.Ordinal)
           .Select(pair => Resolve(model, pair.Key, pair.Value, options))
           .ToList();

    private static TtlSweepTarget Resolve(IModel model, TableRef table, TtlSettings settings, TtlSweepOptions options)
    {
        if (!settings.Enabled || settings.ExpirationExpression is not { Length: > 0 } column)
            return TtlSweepTarget.Refused(table, "the declaration carries no expiration column");

        if (!PlainIdentifier.IsMatch(column))
            return TtlSweepTarget.Refused(table,
                $"the expiration expression '{column}' is not a plain column name. WithTTL can only " +
                "produce one, so this came from somewhere else, and the sweeper will not build a " +
                "DELETE predicate out of SQL it did not write.");

        if (FindColumn(model, table, column) is not { } property)
            return TtlSweepTarget.Refused(table,
                $"the model declares a TTL on \"{column}\" but no property maps to that column, so the " +
                "sweeper could not check what writes it. Refusing rather than guessing: this is the " +
                "one check that stands between a mis-declared TTL and an emptied table.");

        if (WrittenByTheDatabaseAtInsert(property) is { } generated)
            return TtlSweepTarget.Refused(table,
                $"\"{column}\" is filled in by the database when the row is written ({generated}), so " +
                $"every row in {table} is already past it the instant it exists and this predicate " +
                "selects the entire table. That is what CockroachDB would do with the same declaration; " +
                "it is not what anybody wants. Point WithTTL at the column that holds the deadline.");

        var batchSize = ResolveBatchSize(settings, options);

        return new TtlSweepTarget
        {
            Table            = table,
            ExpirationColumn = column,
            BatchSize        = batchSize,
            BatchDelay       = ResolveBatchDelay(settings, batchSize, options),
            RowBudget        = Math.Max(options.RowBudgetPerTable, batchSize),
            DeclaredCron     = settings.JobCron
        };
    }

    /// <summary>The property behind one column of one table, or <c>null</c> if nothing maps to it.</summary>
    /// <remarks>
    /// Walked rather than looked up, because the mapping runs the other way: a property knows its
    /// column for a given store object, and there is no column-keyed index on an entity type. Two
    /// entity types on one table are fine here — TPH means several of them share a table, and any of
    /// them that carries the column carries the same column.
    /// </remarks>
    private static IProperty? FindColumn(IModel model, TableRef table, string column)
    {
        foreach (var entityType in model.GetEntityTypes())
        {
            if (entityType.GetTableName() is not { Length: > 0 } name)
                continue;

            if (!string.Equals(name, table.Name, StringComparison.Ordinal))
                continue;

            if (!string.Equals(entityType.GetSchema() ?? TableRef.DefaultSchema, table.Schema, StringComparison.Ordinal))
                continue;

            var storeObject = StoreObjectIdentifier.Table(name, entityType.GetSchema());

            foreach (var property in entityType.GetProperties())
            {
                if (string.Equals(property.GetColumnName(storeObject), column, StringComparison.Ordinal))
                    return property;
            }
        }

        return null;
    }

    /// <summary>
    /// Whether the store, rather than the application, decides this column's value on insert — and if
    /// so, in what words to say it.
    /// </summary>
    /// <remarks>
    /// <para><b>This exists because of <c>user_friend_requests</c>.</b> <c>FriendRequestEntity</c>
    /// carries two timestamps: <c>RequestedAt</c>, which is <c>HasDefaultValueSql("now()")</c> plus
    /// <c>ValueGeneratedOnAdd</c>, and <c>ExpiredAt</c>, a <c>DateOnly</c> set six months out and put
    /// through <c>AsTTlField</c> — a helper whose only purpose is to make a column usable as a TTL
    /// column. The <c>WithTTL</c> call three lines below it names <c>RequestedAt</c>. Under
    /// CockroachDB's own rule that is "expired when RequestedAt is in the past", which every row is on
    /// arrival, so the declaration as written asks both engines to delete every friend request on the
    /// first run.</para>
    ///
    /// <para>The check is on the model rather than on a list of table names, so it keeps working for
    /// the next entity somebody wires up the same way. Two signals rather than one because they are
    /// preserved differently: <c>ValueGenerated</c> is core metadata and survives into the runtime
    /// model unconditionally, while <c>Relational:DefaultValueSql</c> is a relational annotation that
    /// mainly matters at design time — relying on the second alone would make this quietly stop
    /// checking. Either one firing is enough.</para>
    /// </remarks>
    private static string? WrittenByTheDatabaseAtInsert(IProperty property)
    {
        if (property.GetDefaultValueSql() is { Length: > 0 } defaultSql &&
            CurrentTimeDefaults.Contains(Whitespace.Replace(defaultSql.Trim(), "")))
            return $"its column default is {defaultSql.Trim()}";

        return property.ValueGenerated is ValueGenerated.OnAdd or ValueGenerated.OnAddOrUpdate
            ? $"it is {property.ValueGenerated}, so the store supplies the value"
            : null;
    }

    /// <summary>
    /// Rows per statement: the smaller of the two declared batch sizes, or the configured default.
    /// </summary>
    /// <remarks>
    /// The smaller of the two because one statement here does the work CockroachDB splits between a
    /// select batch and a delete batch, and taking the larger would exceed whichever bound the author
    /// actually cared about. In this model the point is moot — <c>WithTTL</c> takes one
    /// <c>batchSize</c> and writes it to both — which is exactly why the rule should be the
    /// conservative one: nothing here proves it wrong, so nothing will notice if it is.
    /// </remarks>
    private static int ResolveBatchSize(TtlSettings settings, TtlSweepOptions options)
    {
        int?[] declared = [settings.SelectBatchSize, settings.DeleteBatchSize];

        var size = declared.Where(value => value is > 0).Select(value => value!.Value).ToArray();

        return Math.Max(1, size.Length == 0 ? options.DefaultBatchSize : size.Min());
    }

    /// <summary>How long to wait after a batch, honouring the declared rate limit but never below the floor.</summary>
    /// <remarks>
    /// <c>ttl_delete_rate_limit</c> is rows per second, so the pause a batch owes is its size divided
    /// by that rate. Both invite tables declare <c>52428800</c>, which is a byte count that wandered
    /// into a row-count knob; as a rate it yields a pause of about a tenth of a millisecond, i.e. none.
    /// The floor is therefore what actually paces the sweep, and it must not be removed on the grounds
    /// that "the annotation already has a rate limit".
    /// </remarks>
    private static TimeSpan ResolveBatchDelay(TtlSettings settings, int batchSize, TtlSweepOptions options)
    {
        if (settings.DeleteRateLimit is not > 0)
            return options.MinimumBatchDelay;

        var derived = TimeSpan.FromSeconds((double)batchSize / settings.DeleteRateLimit.Value);

        return derived > options.MinimumBatchDelay ? derived : options.MinimumBatchDelay;
    }
}
