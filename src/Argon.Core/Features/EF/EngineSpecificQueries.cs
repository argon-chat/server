namespace Argon.Features.EF;

using System.Globalization;
using Microsoft.EntityFrameworkCore.Infrastructure;
using Microsoft.EntityFrameworkCore.Metadata;
using Microsoft.EntityFrameworkCore.Storage;
using Npgsql;

/// <summary>
/// The SQL that LINQ cannot express. Grains and services go through these helpers and never write
/// SQL themselves (analyzer RS0030, src/BannedSymbols.txt); table names always come from the EF model.
/// </summary>
public static class EngineSpecificQueries
{
    /// <summary>
    /// The optimizer's row estimate for each entity's table that has one. A table without statistics
    /// yet (PostgreSQL reports -1 until the first ANALYZE) is absent from the answer.
    /// </summary>
    public static async Task<Dictionary<IEntityType, long>> EstimateRowCountsAsync(this DbContext db, DatabaseProviderKind kind,
        IReadOnlyCollection<IEntityType> entities, CancellationToken ct = default)
    {
        var byTable = entities.ToDictionary(e => e.GetTableName()!);
        var tables  = byTable.Keys.ToArray();

        var query = kind is DatabaseProviderKind.CockroachDb
            ? db.Database.SqlQuery<TableRowEstimate>(
                $"""
                 SELECT table_name AS "Name", estimated_row_count AS "Rows"
                 FROM crdb_internal.table_row_statistics
                 WHERE estimated_row_count IS NOT NULL AND table_name = ANY({tables})
                 """)
            : db.Database.SqlQuery<TableRowEstimate>(
                $"""
                 SELECT c.relname AS "Name", c.reltuples::bigint AS "Rows"
                 FROM pg_class c JOIN pg_namespace n ON n.oid = c.relnamespace
                 WHERE n.nspname = current_schema() AND c.reltuples >= 0 AND c.relname = ANY({tables})
                 """);

        var rows = await query.ToListAsync(ct);

        return rows.DistinctBy(r => r.Name).ToDictionary(r => byTable[r.Name], r => r.Rows);
    }

    /// <summary>
    /// A transaction whose reads are served as of <paramref name="lag"/> ago, or null on engines
    /// without historical reads. On CockroachDB such reads neither wait on intents nor push concurrent
    /// writers into 40001 retries. Must be the first thing done in the transaction; a table younger
    /// than the timestamp fails with 42P01.
    /// </summary>
    public static async Task<IDbContextTransaction?> BeginHistoricalReadAsync(this DatabaseFacade database, DatabaseProviderKind kind,
        TimeSpan lag, CancellationToken ct = default)
    {
        if (kind is not DatabaseProviderKind.CockroachDb)
            return null;

        var tx      = await database.BeginTransactionAsync(ct);
        var seconds = Math.Max(1, (int)Math.Ceiling(lag.TotalSeconds)).ToString(CultureInfo.InvariantCulture);

        await database.ExecuteSqlRawAsync("SET TRANSACTION AS OF SYSTEM TIME '-" + seconds + "s'", ct);

        return tx;
    }

    public static bool IsUniqueViolation(this DbUpdateException exception)
        => exception.InnerException is PostgresException { SqlState: PostgresErrorCodes.UniqueViolation };

    private sealed class TableRowEstimate
    {
        public string Name { get; init; } = "";
        public long   Rows { get; init; }
    }
}
