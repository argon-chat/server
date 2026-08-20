namespace Argon.Features.EF;

/// <summary>
/// How much authority a change needs before anything may run it.
/// </summary>
/// <remarks>
/// <para>The boundaries are drawn by blast radius, not by convenience, and the asymmetry between the
/// three is the whole point: adding a TTL, removing one, and re-pacing one that already exists are
/// three different acts that happen to share a statement shape.</para>
/// </remarks>
public enum SchemaChangeTier
{
    /// <summary>
    /// Re-pacing a TTL that is already on and already expires the same rows: the cron, the batch sizes,
    /// the delete rate limit.
    /// </summary>
    /// <remarks>
    /// Safe to run unattended because it cannot change <em>which</em> rows die, only how quickly the
    /// job that was already deleting them gets through its work. The rows in question are ones the
    /// model has been asking to delete since the table was created. Cheapest first in the plan, so a
    /// pass that is interrupted has done the least alarming things.
    /// </remarks>
    Automatic,

    /// <summary>
    /// Turning a TTL on, or changing which column decides that a row has expired.
    /// </summary>
    /// <remarks>
    /// <para>Never on the boot path, whatever the mode says, and the reason is that this is a
    /// data-deleting statement wearing a schema change's clothes. The moment CockroachDB accepts it, it
    /// schedules a job that deletes <em>every</em> row already past its expiration — which on a table
    /// that has been accumulating since before anyone declared a TTL is the entire backlog, on the
    /// first run, at whatever hour the cron says. That is a decision with a date on it, not something a
    /// pod does to itself while it boots.</para>
    ///
    /// <para>Changing the expiration expression is the same act in disguise: it re-decides which rows
    /// are already expired, and the answer can be "all of them".</para>
    /// </remarks>
    Approval,

    /// <summary>
    /// Reported, never issued, in any mode, with no flag that enables it.
    /// </summary>
    /// <remarks>
    /// <para>Three things land here. <b>Turning a TTL off</b>, because a TTL the server has and the
    /// model does not is not evidence that nobody wanted it — converging downward is how a reconciler
    /// becomes a categorically more dangerous object than the problem it was written for.
    /// <b>Anything on a table carrying <c>ttl_pause</c></b>, because that is the operator's kill switch
    /// for a delete job that is eating the cluster and the reconciler must not fight it during the
    /// incident it was pulled in. And <b>moving between <c>ttl_expire_after</c> and
    /// <c>ttl_expiration_expression</c></b>, because the first is backed by a hidden
    /// <c>crdb_internal_expiration</c> column: switching adds or drops a column and rewrites the table,
    /// which is a migration, not a reconcile.</para>
    /// </remarks>
    Refused
}

/// <summary>Where one declared table stands.</summary>
public enum SchemaTtlStatus
{
    /// <summary>The server's TTL is what the model declares.</summary>
    Converged,

    /// <summary>The table does not exist yet. Its <c>CREATE TABLE</c> will carry the clause; nothing to do.</summary>
    Absent,

    /// <summary>The server could not be read, or its answer could not be understood. Never "converged".</summary>
    Undetermined,

    /// <summary>The server differs from the declaration. <see cref="SchemaTtlItem.Tier"/> says who may fix it.</summary>
    Drift
}

/// <summary>One table's verdict, and the statement that would settle it.</summary>
/// <param name="Statement">
/// The SQL, or <c>null</c> when there is nothing runnable — a missing table, an unreadable one, or a
/// refusal whose whole point is that no statement should exist for it.
/// </param>
/// <param name="Notes">
/// Things worth printing that are not drift: parameters the server carries and the model has no opinion
/// about. Informational on purpose — see <see cref="SchemaTtlPlan.Build"/>.
/// </param>
public sealed record SchemaTtlItem(
    TableRef Table,
    SchemaTtlStatus Status,
    SchemaChangeTier Tier,
    string Reason,
    string? Statement,
    IReadOnlyList<string> Notes)
{
    public bool IsActionable => Statement is not null;
}

/// <summary>
/// The diff between what the model declares and what the server reports, as an ordered list of
/// statements plus the reason each one exists.
/// </summary>
/// <remarks>
/// <para>Recomputed from scratch on every pass and never stored. A persisted plan can be wrong in both
/// directions — the process died after issuing and before recording, so it looks pending while it is
/// running; or the record says done and the background job failed afterwards — and re-deriving from
/// the catalog is the only thing that is right in both. It is also what makes a crash at any step
/// recoverable by restarting rather than by repairing.</para>
///
/// <para>Only tables the model declares a TTL for are read at all. That is deliberate and it has a
/// consequence worth stating out loud: <b>deleting a <c>WithTTL</c> call does not remove the TTL from
/// the database.</b> The table drops out of the desired set, stops being read, and the server keeps
/// doing what it was last told. Removing a TTL is a human operation, for the same reason turning one
/// off is <see cref="SchemaChangeTier.Refused"/> — the alternative is a reconciler that quietly stops
/// deleting rows a table was designed to shed, and nothing observable changes until the disk fills.</para>
/// </remarks>
public sealed record SchemaTtlPlan(IReadOnlyList<SchemaTtlItem> Items)
{
    public static readonly SchemaTtlPlan Empty = new([]);

    /// <summary>Nothing to do and nothing unknown. The only state that may be reported as healthy.</summary>
    public bool IsConverged
        => Items.All(item => item.Status is SchemaTtlStatus.Converged or SchemaTtlStatus.Absent);

    public bool HasUndetermined => Items.Any(item => item.Status is SchemaTtlStatus.Undetermined);

    /// <summary>The statements this actor is allowed to run, cheapest first.</summary>
    public IEnumerable<SchemaTtlItem> Runnable(SchemaChangeTier allowed)
        => Items.Where(item => item.IsActionable && item.Tier <= allowed);

    /// <summary>Everything that needs a hand: a bigger tier than the runner has, or a flat refusal.</summary>
    public IEnumerable<SchemaTtlItem> Blocked(SchemaChangeTier allowed)
        => Items.Where(item => item.Status is SchemaTtlStatus.Drift && (!item.IsActionable || item.Tier > allowed));

    /// <summary>
    /// Compares declaration against observation, one table at a time.
    /// </summary>
    /// <remarks>
    /// <para>The comparison is field-wise rather than whole-record, because the two sides do not mean
    /// the same thing by <c>null</c>. On the observed side <c>null</c> is "the server is using its
    /// default"; on the declared side it is "Argon has no opinion", which is what <c>WithTTL</c>'s
    /// <c>0</c> arguments produce and what <c>FriendRequestEntity</c> declares for every batch knob. A
    /// server value where the model has no opinion is therefore a note, not drift — treating it as
    /// drift would make that table report a difference on every boot that no statement could ever
    /// close, since the only statement that would is a <c>RESET</c> of something nobody declared.</para>
    ///
    /// <para>Ordering is fixed rather than incidental: tier ascending so the cheapest, least alarming
    /// statement runs first, then table name so two pods computing the same plan compute the same
    /// order. A plan that shuffles is a plan whose logs cannot be diffed between deploys.</para>
    /// </remarks>
    public static SchemaTtlPlan Build(
        IReadOnlyDictionary<TableRef, TtlSettings> desired,
        IReadOnlyDictionary<TableRef, TtlObservation> observed)
    {
        var items = new List<SchemaTtlItem>(desired.Count);

        foreach (var (table, declared) in desired)
        {
            if (!observed.TryGetValue(table, out var observation))
            {
                items.Add(Undetermined(table, "the table was never read"));
                continue;
            }

            items.Add(observation.Kind switch
            {
                TtlObservationKind.Missing => new SchemaTtlItem(table, SchemaTtlStatus.Absent,
                    SchemaChangeTier.Automatic,
                    "the table does not exist yet; the CREATE TABLE that makes it carries the TTL clause",
                    null, []),

                TtlObservationKind.Unreadable => Undetermined(table, observation.Failure ?? "unreadable"),

                _ => Compare(table, declared, observation.Ttl!)
            });
        }

        return new SchemaTtlPlan(items
           .OrderBy(item => item.Tier)
           .ThenBy(item => item.Table.ToString(), StringComparer.Ordinal)
           .ToList());
    }

    private static SchemaTtlItem Undetermined(TableRef table, string reason)
        => new(table, SchemaTtlStatus.Undetermined, SchemaChangeTier.Refused,
            $"could not determine the current TTL: {reason}", null, []);

    private static SchemaTtlItem Compare(TableRef table, TtlSettings declared, ObservedTtl observed)
    {
        if (observed.Paused)
            return new SchemaTtlItem(table, SchemaTtlStatus.Drift, SchemaChangeTier.Refused,
                "ttl_pause is set on this table. Somebody stopped its delete job on purpose; nothing here " +
                "will touch it until they clear the parameter.",
                null, []);

        if (observed.ExpireAfter is { } expireAfter)
            return new SchemaTtlItem(table, SchemaTtlStatus.Drift, SchemaChangeTier.Refused,
                $"the table expires rows with ttl_expire_after = '{expireAfter}' and the model declares an " +
                "expiration expression. Moving between the two adds or drops the hidden " +
                "crdb_internal_expiration column and rewrites the table, so it is a migration run by a " +
                "human, not a reconcile.",
                null, []);

        var notes = observed.OtherParameters
           .OrderBy(pair => pair.Key, StringComparer.Ordinal)
           .Select(pair => $"the server also carries {pair.Key} = '{pair.Value}', which the model does not declare")
           .Concat(Unopinionated(declared, observed.Settings))
           .ToList();

        if (!observed.Settings.Enabled)
            return new SchemaTtlItem(table, SchemaTtlStatus.Drift, SchemaChangeTier.Approval,
                $"the table has no row-level TTL and the model declares one ({SchemaTtlModel.Describe(declared)}). " +
                "Enabling it schedules deletion of every row that is already past its expiration.",
                Alter(table, EnableParameters(declared)), notes);

        if (observed.Settings.ExpirationExpression != declared.ExpirationExpression)
            return new SchemaTtlItem(table, SchemaTtlStatus.Drift, SchemaChangeTier.Approval,
                $"rows expire on \"{observed.Settings.ExpirationExpression}\" and the model declares " +
                $"\"{declared.ExpirationExpression}\". Changing it re-decides which rows are already expired.",
                Alter(table, [ExpirationParameter(declared)]), notes);

        var tuning = TuningParameters(declared, observed.Settings);

        if (tuning.Count == 0)
            return new SchemaTtlItem(table, SchemaTtlStatus.Converged, SchemaChangeTier.Automatic,
                "the server's TTL matches the declaration", null, notes);

        return new SchemaTtlItem(table, SchemaTtlStatus.Drift, SchemaChangeTier.Automatic,
            $"the TTL is on and expires the right rows; its pacing differs ({string.Join("; ", tuning.Select(p => p.Description))})",
            Alter(table, tuning.Select(p => p.Sql).ToList()), notes);
    }

    /// <summary>
    /// Server values for parameters the model deliberately left alone.
    /// </summary>
    /// <remarks>
    /// Reported rather than swallowed, and that distinction only becomes visible once the batch knobs
    /// are read: a value the model has no opinion about is dropped out of the comparison, so without
    /// this it would vanish entirely and the table would look untouched. Somebody set it on purpose —
    /// the only statement that would "close" it is a <c>RESET</c> of a value nobody declared — so it is
    /// printed and left alone.
    /// </remarks>
    private static IEnumerable<string> Unopinionated(TtlSettings declared, TtlSettings observed)
    {
        if (declared.SelectBatchSize is null && observed.SelectBatchSize is { } select)
            yield return $"the server sets ttl_select_batch_size = {select}; the model leaves it to the server";

        if (declared.DeleteBatchSize is null && observed.DeleteBatchSize is { } delete)
            yield return $"the server sets ttl_delete_batch_size = {delete}; the model leaves it to the server";

        if (declared.DeleteRateLimit is null && observed.DeleteRateLimit is { } rate)
            yield return $"the server sets ttl_delete_rate_limit = {rate}; the model leaves it to the server";
    }

    /// <summary>
    /// Everything needed to switch a TTL on, in one statement.
    /// </summary>
    /// <remarks>
    /// <c>ttl = 'on'</c> is deliberately not emitted. CockroachDB derives it from the presence of an
    /// expiration source, and naming it explicitly is at best redundant and at worst rejected depending
    /// on the version — while setting the expression is unambiguous on every version that has row-level
    /// TTL at all.
    /// </remarks>
    private static List<string> EnableParameters(TtlSettings declared)
    {
        var parameters = new List<string> { ExpirationParameter(declared), $"ttl_job_cron = {Literal(declared.JobCron)}" };

        if (declared.SelectBatchSize is { } select)
            parameters.Add($"ttl_select_batch_size = {select}");
        if (declared.DeleteBatchSize is { } delete)
            parameters.Add($"ttl_delete_batch_size = {delete}");
        if (declared.DeleteRateLimit is { } rate)
            parameters.Add($"ttl_delete_rate_limit = {rate}");

        return parameters;
    }

    /// <summary>
    /// Only the pacing parameters that actually differ, and only ones the model declares.
    /// </summary>
    /// <remarks>
    /// Only the differing ones so the statement in the log says what it changed rather than restating
    /// the whole TTL; only the declared ones because a parameter the model leaves at <c>0</c> is an
    /// absence of an opinion, and the statement that would "fix" it is a <c>RESET</c> of a value
    /// somebody chose deliberately.
    /// </remarks>
    private static List<(string Sql, string Description)> TuningParameters(TtlSettings declared, TtlSettings observed)
    {
        var changes = new List<(string Sql, string Description)>();

        if (declared.JobCron != observed.JobCron)
            Add("ttl_job_cron", Literal(declared.JobCron), declared.JobCron, observed.JobCron);

        AddNumber("ttl_select_batch_size", declared.SelectBatchSize, observed.SelectBatchSize);
        AddNumber("ttl_delete_batch_size", declared.DeleteBatchSize, observed.DeleteBatchSize);
        AddNumber("ttl_delete_rate_limit", declared.DeleteRateLimit, observed.DeleteRateLimit);

        return changes;

        void AddNumber(string name, int? want, int? have)
        {
            if (want is not null && want != have)
                Add(name, want.Value.ToString(), want.Value.ToString(), have?.ToString());
        }

        void Add(string name, string sqlValue, string want, string? have)
            => changes.Add(($"{name} = {sqlValue}", $"{name} is '{have ?? "server default"}', declared '{want}'"));
    }

    /// <summary>
    /// The expiration expression as a storage parameter.
    /// </summary>
    /// <remarks>
    /// Doubly wrapped, and both wrappings matter: the column identifier is delimited so its mixed case
    /// survives, and the result is then a SQL string literal because that is what the parameter takes.
    /// Dropping the inner quotes would fold <c>ExpireAt</c> to <c>expireat</c> and address a column
    /// that does not exist; dropping the outer ones is a syntax error.
    /// </remarks>
    private static string ExpirationParameter(TtlSettings declared)
        => $"ttl_expiration_expression = {Literal(TableRef.Delimit(declared.ExpirationExpression!))}";

    private static string Alter(TableRef table, IReadOnlyList<string> parameters)
        => $"ALTER TABLE {table.Quoted} SET ({string.Join(", ", parameters)})";

    private static string Literal(string value) => $"'{value.Replace("'", "''")}'";
}
