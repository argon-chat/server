namespace Argon.Features.EF;

using Microsoft.EntityFrameworkCore.Metadata;
using Npgsql;
using System.Data.Common;
using System.Text.RegularExpressions;

/// <summary>
/// Issues the two table declarations the model carries — <c>Regional:Locality</c> and
/// <c>Job:Expiration</c> — against a database whose tables already exist.
/// </summary>
/// <remarks>
/// <para><b>Why this exists.</b> <c>MultiregionalMigrationsSqlGenerator</c> writes both clauses from
/// exactly one place, its <c>CreateTableOperation</c> override, and EF emits no operation at all when
/// an annotation changes on a table that is already there —
/// <c>DbLocalityTests.Changing_a_locality_after_the_table_exists_produces_nothing</c> pins that on
/// purpose. So a declaration reaches a table created after it was written and never reaches one
/// created before it, which is every long-lived table in production.</para>
///
/// <para><b>Why it is this small, and what it replaced.</b> There was a reconciler here: it read the
/// declarations, read the server back with <c>SHOW CREATE TABLE</c>, normalised two spellings of one
/// physical state on each side, diffed them, sorted the differences into tiers, converged under its
/// own lease and reported a verdict on <c>/health</c>. Every part of that existed to serve one
/// decision — whether turning a TTL on for the first time, and so deleting the accumulated expired
/// backlog in one pass, was allowed to happen unattended. The owner has since decided that it is. With
/// that gone the observed-state read has no consumer: reading the server only ever answered "do I need
/// to issue this", and the answer is now always yes.</para>
///
/// <para><b>What that costs, said out loud.</b> Every statement below is re-issued on every boot that
/// reaches this point, whether or not the server already agrees. Both shapes are descriptor and zone
/// edits rather than data rewrites — <c>SET LOCALITY GLOBAL</c> on an already-global table and a TTL
/// <c>SET</c> that changes no value cost a schema-change job, not a backfill — and this runs under the
/// migration lease, so it is one pod per boot wave rather than all of them. Re-reading the catalog to
/// avoid those no-ops is precisely the machinery that was retired; do not bring it back without a
/// measurement that says the jobs are the problem.</para>
/// </remarks>
public static class SchemaDeclarations
{
    /// <summary>
    /// Log the statements instead of running them. Defaults to <c>false</c> — the step applies.
    /// </summary>
    /// <remarks>
    /// The direction of that default is the change. What was here defaulted to report-only, and a step
    /// that only reports leaves the declarations exactly where they have been since somebody wrote
    /// them: in the model, and in no database. The dry run exists so an operator can read the exact
    /// statements against one deployment before a rollout, not as a resting state to forget about.
    /// </remarks>
    public const string DryRunKey = "Database:Declarations:DryRun";

    /// <summary>Unset, empty or unparsable all mean "apply", which is what this exists to do.</summary>
    public static bool IsDryRun(IConfiguration configuration)
        => bool.TryParse(configuration[DryRunKey], out var dryRun) && dryRun;

    /// <summary>
    /// What the model declares about placement, keyed by the table the statement will name.
    /// </summary>
    /// <remarks>
    /// <para>Keyed by <see cref="IReadOnlyEntityType.GetTableName"/> and never by the <c>DbSet</c>
    /// property or the CLR type, because in this model they differ: <c>SpaceMemberEntity</c> maps to
    /// <c>UsersToServerRelations</c>, and <c>ALTER TABLE "SpaceMemberEntity"</c> would come back as a
    /// relation that does not exist.</para>
    ///
    /// <para>Two entity types on one table declaring different placements throws, naming both, rather
    /// than taking the first. TPH puts several entity types on one table here, and
    /// <c>MultiregionalMigrationsSqlGenerator</c> resolves that with a <c>FirstOrDefault</c> over
    /// model-build order — which would make where the data physically lives depend on the order entity
    /// configurations happened to be registered in, silently. Compared ordinally: both spellings come
    /// out of the same three helpers, so any difference at all is a disagreement worth stopping on.</para>
    /// </remarks>
    /// <exception cref="InvalidOperationException">
    /// Two entity types on one table declare different placements.
    /// </exception>
    public static IReadOnlyDictionary<TableRef, string> ReadLocalities(IModel model)
    {
        var declarations = new Dictionary<TableRef, (string Owner, string Locality)>();

        foreach (var entityType in model.GetEntityTypes())
        {
            // Owned types, query types and anything mapped to a view have no table to alter.
            if (entityType.GetTableName() is not { Length: > 0 } table)
                continue;

            if (entityType.FindAnnotation(DbLocalityExtensions.LocalityAnnotationKey)?.Value
                is not string { Length: > 0 } locality)
                continue;

            var key = new TableRef(entityType.GetSchema() ?? TableRef.DefaultSchema, table);

            if (!declarations.TryGetValue(key, out var existing))
            {
                declarations[key] = (entityType.Name, locality);
                continue;
            }

            if (string.Equals(existing.Locality, locality, StringComparison.Ordinal))
                continue;

            throw new InvalidOperationException(
                $"'{existing.Owner}' and '{entityType.Name}' both map to table {key} and declare different " +
                $"placements ('{existing.Locality}' vs '{locality}'). One table has one locality; resolve " +
                "the disagreement in the entity configurations rather than letting model-build order decide " +
                "which region the data physically lives in.");
        }

        return declarations.ToDictionary(pair => pair.Key, pair => pair.Value.Locality);
    }

    /// <summary>
    /// The statement that places a table, or <c>null</c> when the declaration is one this refuses.
    /// </summary>
    /// <remarks>
    /// <para><b><c>REGIONAL BY ROW</c> is never emitted, and this guard is a feature rather than an
    /// omission.</b> Converting a populated table to it is not a metadata edit: CockroachDB adds a
    /// hidden <c>crdb_region</c> column, puts it at the front of the primary key, partitions the table
    /// and every secondary index by it, backfills all of that, and homes every existing row in the
    /// primary region. It is close to one-way, on <c>Messages</c> it rewrites the largest table in the
    /// product, and the result would be wrong anyway — the right conversion derives the region from
    /// Argon's own UUIDv7 region tag through a computed column, so rows land where they were actually
    /// written. That is a staged operation an operator runs with a plan, not something a pod does to
    /// itself while it boots. Deleting this guard to "finish the feature" is the mistake it exists to
    /// prevent.</para>
    ///
    /// <para>Matched on the leading words rather than on equality, so that a future
    /// <c>REGIONAL BY ROW AS "region"</c> — the computed-column form the staged conversion will
    /// use — is refused by the same line instead of slipping past it.</para>
    /// </remarks>
    public static string? PlacementStatement(TableRef table, string locality)
        => IsRegionalByRow(locality)
            ? null
            : $"ALTER TABLE {table.Quoted} SET LOCALITY {locality.Trim()}";

    private static readonly Regex Whitespace = new(@"\s+", RegexOptions.Compiled);

    /// <summary>Whether a declaration is the one shape that is never issued.</summary>
    private static bool IsRegionalByRow(string locality)
        => Whitespace.Replace(locality.Trim(), " ")
           .StartsWith("REGIONAL BY ROW", StringComparison.OrdinalIgnoreCase);

    /// <summary>The statement that gives a table the row-level TTL the model declares.</summary>
    public static string TtlStatement(TableRef table, TtlSettings ttl)
        => $"ALTER TABLE {table.Quoted} SET ({string.Join(", ", TtlParameters(ttl))})";

    /// <remarks>
    /// <c>ttl = 'on'</c> is deliberately not emitted: CockroachDB derives it from the presence of an
    /// expiration source, and naming it explicitly is redundant at best and rejected at worst depending
    /// on the version. The batch knobs are emitted only when the model has an opinion — <c>WithTTL</c>
    /// defaults them to <c>0</c>, which <see cref="TtlSettings"/> reads as "leave the server's default
    /// alone", and writing a literal zero would switch off pacing the server was doing correctly.
    /// </remarks>
    private static IEnumerable<string> TtlParameters(TtlSettings ttl)
    {
        // Doubly wrapped, and both wrappings matter: the column identifier is delimited so its mixed
        // case survives, and the result is then a SQL string literal because that is what the parameter
        // takes. Dropping the inner quotes folds ExpireAt to expireat and addresses a column that does
        // not exist; dropping the outer ones is a syntax error. Non-null by construction —
        // SchemaTtlModel.Parse refuses a declaration with no expiration column, and it is the only
        // thing that builds one of these.
        yield return $"ttl_expiration_expression = {Literal(TableRef.Delimit(ttl.ExpirationExpression!))}";
        yield return $"ttl_job_cron = {Literal(ttl.JobCron)}";

        if (ttl.SelectBatchSize is { } select)
            yield return $"ttl_select_batch_size = {select}";

        if (ttl.DeleteBatchSize is { } delete)
            yield return $"ttl_delete_batch_size = {delete}";

        if (ttl.DeleteRateLimit is { } rate)
            yield return $"ttl_delete_rate_limit = {rate}";
    }

    private static string Literal(string value) => $"'{value.Replace("'", "''")}'";

    /// <summary>
    /// How many regions the database has, or zero when it is not multi-region at all.
    /// </summary>
    /// <remarks>
    /// One targeted read rather than the catalog parser this step replaced: the question is a count, the
    /// answer is a number, and nothing here needs to know what any individual table currently says.
    /// A database that was never given a primary region has no crdb_internal.regions rows to return, so
    /// zero and "not multi-region" are the same answer and want the same behaviour.
    /// </remarks>
    private async static Task<int> RegionCountAsync(DbConnection connection, CancellationToken ct)
    {
        await using var command = connection.CreateCommand();

        command.CommandText = "SELECT count(*) FROM [SHOW REGIONS FROM DATABASE]";

        try
        {
            return Convert.ToInt32(await command.ExecuteScalarAsync(ct));
        }
        catch (DbException)
        {
            // Not multi-region, so the statement itself is an error rather than an empty result. That is
            // the answer, not a failure: nothing below applies to such a database.
            return 0;
        }
    }

    /// <summary>
    /// Reads both declarations off the live model and issues them, or logs them under
    /// <paramref name="dryRun"/>.
    /// </summary>
    /// <remarks>
    /// <para>The dry run is this same method with one branch at the point of execution, not a second
    /// implementation. A separate "what would it do" path is how a green preview and a wrong apply come
    /// to coexist.</para>
    ///
    /// <para>Statements are ordered by table within each annotation so two pods compute the same order
    /// and two boots produce diffable logs. Each is issued alone on a raw command: CockroachDB supports
    /// DDL inside an explicit transaction only for <c>CREATE TABLE</c> and <c>CREATE INDEX</c>, and
    /// going through EF would put <c>EnableRetryOnFailure</c> behind a statement whose outcome after a
    /// timeout is not knowable.</para>
    ///
    /// <para>A statement the server refuses is logged and the rest still run — see the catch below for
    /// why — while anything else (a dropped connection, a model that will not read) is left to the
    /// caller, which logs it and boots anyway.</para>
    /// </remarks>
    public async static Task ApplyAsync(
        DbContext dbContext,
        DbConnection connection,
        bool dryRun,
        ILogger logger,
        CancellationToken ct = default)
    {
        // Probed, not declared. 'Database:Provider' resolves to CockroachDb when it is unset or
        // misspelled, and this is a thing that emits ALTER — a fail-open in that direction would send
        // Cockroach-only DDL at a PostgreSQL server.
        if (await DatabaseEngineProbe.DetectAsync(connection, ct) is not DatabaseProviderKind.CockroachDb)
        {
            // Said out loud rather than skipped in silence: a step that is quiet on an engine it cannot
            // act on is indistinguishable from one that is broken. Once, because this runs once per boot.
            logger.LogInformation(
                "Table placement and row-level TTL are CockroachDB syntax and this server is PostgreSQL; " +
                "no table declarations were issued. Row expiry is handled here by TtlSweepGrain instead");

            return;
        }

        // And then ask the database how many regions it actually has, because placement only means
        // something once there is more than one.
        //
        // Two failures are refused here, and they are opposite in shape. A database with no primary
        // region rejects every SET LOCALITY outright, so a deployment that provisioned its own database
        // — the multi-region CREATE DATABASE path runs only when the database is absent — would log a
        // dozen errors on every boot, forever, and apply nothing. And a database with exactly one region
        // accepts LOCALITY GLOBAL and charges the full price for it: global tables commit into the
        // future and the caller waits out the closed-timestamp lead, hundreds of milliseconds per write,
        // in exchange for a cross-region read that does not exist yet. On registration, profile edits,
        // role grants and channel creation that is a pure regression.
        //
        // So the gate is the region count, not a flag somebody has to remember to set. It opens by
        // itself on the day a second region is added, which is also the day the trade turns positive.
        // Narrowly: PLACEMENT waits, row-level TTL does not. Expiry is about how long a row lives and has
        // nothing to do with geography, so gating it here would withhold a correct, cheap change for an
        // unrelated reason.
        var regions = await RegionCountAsync(connection, ct);

        var statements = new List<string>();

        if (regions < 2)
            logger.LogInformation(
                "The database reports {Regions} region(s); table placement is not applied below two. " +
                "LOCALITY GLOBAL costs a commit-wait on every write and pays it back only in cross-region " +
                "reads, so it is left alone until there is a second region to read from. Row-level TTL is " +
                "unaffected and still applies",
                regions);
        else
            foreach (var (table, locality) in ReadLocalities(dbContext.Model).OrderBy(pair => pair.Key.ToString(), StringComparer.Ordinal))
            {
                if (PlacementStatement(table, locality) is { } statement)
                    statements.Add(statement);
                else
                    logger.LogInformation(
                        "{Table} declares '{Locality}', which this step never issues: converting a populated " +
                        "table adds crdb_region to the primary key, repartitions every index and homes every " +
                        "existing row in the primary region. It is a staged operator-run conversion",
                        table, locality);
            }

        foreach (var (table, ttl) in SchemaTtlModel.ReadDesiredState(dbContext.Model).OrderBy(pair => pair.Key.ToString(), StringComparer.Ordinal))
            statements.Add(TtlStatement(table, ttl));

        if (statements.Count == 0)
        {
            logger.LogInformation("The model declares no table placement and no row-level TTL");
            return;
        }

        var refused = 0;

        foreach (var statement in statements)
        {
            if (dryRun)
            {
                logger.LogInformation("Would apply ({Key}=true): {Sql}", DryRunKey, statement);
                continue;
            }

            logger.LogInformation("Applying table declaration: {Sql}", statement);

            try
            {
                await using var command = connection.CreateCommand();

                command.CommandText = statement;

                await command.ExecuteNonQueryAsync(ct);
            }
            catch (PostgresException e)
            {
                // Per statement, and never retried. Every statement names a different table, so one the
                // server refuses — a table the migrations have not created yet, a region the cluster
                // does not have — is not a reason the other eleven should stay unplaced on every boot
                // from here on. Not retried because the outcome of a statement that timed out is not
                // knowable from its error, and the next boot re-issues the whole list anyway. Anything
                // that is not a server refusal is left to propagate: a dropped connection is not a
                // property of this statement and the ones after it will not fare better.
                refused++;
                logger.LogError(e, "CockroachDB refused {Sql} ({SqlState})", statement, e.SqlState);
            }
        }

        if (refused > 0)
            logger.LogError(
                "{Refused} of {Count} table declaration(s) were refused; the pod continues and the next " +
                "boot will re-issue all of them", refused, statements.Count);
        else
            logger.LogInformation(
                "{Count} table declaration(s) {Verb}", statements.Count, dryRun ? "would be applied" : "applied");
    }
}
