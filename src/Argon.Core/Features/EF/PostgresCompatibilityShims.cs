namespace Argon.Core.Features.EF;

using Microsoft.EntityFrameworkCore;

/// <summary>
/// Teaches a vanilla PostgreSQL server the handful of CockroachDB built-ins that Argon's migration
/// history bakes into column defaults.
/// <para>
/// The alternative — rewriting a hundred already-applied migrations — would change the SQL that
/// production has run, and would have to be redone every time a new migration is scaffolded against
/// Cockroach. Defining the missing functions instead keeps a single migration history that both
/// engines execute byte-for-byte identically.
/// </para>
/// </summary>
public static class PostgresCompatibilityShims
{
    /// <summary>
    /// CockroachDB's <c>unique_rowid()</c> returns a unique, broadly time-ordered int64 without
    /// coordination. This reproduces the shape of it: 43 bits of millisecond clock in the high bits
    /// followed by 20 bits from a sequence, which keeps values ascending, collision-free across
    /// concurrent sessions, and comfortably inside a signed 64-bit integer until well past year 2100.
    /// </summary>
    public const string UniqueRowIdFunction =
        """
        CREATE SEQUENCE IF NOT EXISTS argon_unique_rowid_seq AS bigint CYCLE;

        CREATE OR REPLACE FUNCTION unique_rowid() RETURNS bigint
            LANGUAGE sql
            VOLATILE
        AS $$
            SELECT ((EXTRACT(EPOCH FROM clock_timestamp()) * 1000)::bigint << 20)
                 | (nextval('argon_unique_rowid_seq') & 1048575);
        $$;
        """;

    /// <summary>
    /// Installs every shim. Idempotent, and cheap enough to run on each start-up rather than
    /// tracking whether it has been applied before.
    /// </summary>
    public async static Task ApplyAsync(DbContext ctx, ILogger logger, CancellationToken ct = default)
    {
        await ctx.Database.ExecuteSqlRawAsync(UniqueRowIdFunction, ct);
        logger.LogInformation("PostgreSQL compatibility shims applied (unique_rowid)");
    }
}
