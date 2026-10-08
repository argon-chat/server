namespace Argon.Features.EF;

using System.Data.Common;

/// <summary>
/// Asks the server what it is, instead of believing what configuration claims.
/// </summary>
/// <remarks>
/// <para><c>Database:Provider</c> has to be read before any connection exists — it selects the
/// migrations SQL generator, and that is fixed when the context is built. So the declaration cannot be
/// replaced by a probe. What it can be is <em>checked</em>, once, against the connection the boot path
/// already opens.</para>
///
/// <para>Checking matters because the declaration fails open in the dangerous direction: an unset or
/// unparsable key resolves to <c>CockroachDb</c>, the shipped <c>appsettings.json</c> carries no
/// <c>Provider</c> key at all, and <c>tests/README.md</c> documented the value as <c>Postgres</c>,
/// which is not a member of the enum and therefore parses to nothing. A PostgreSQL deployment set up
/// from this repository's own documentation announces itself as CockroachDB. Today that only picks a
/// generator whose Cockroach-only clauses never fire; the moment anything issues Cockroach DDL on the
/// strength of that flag, it issues it at Postgres.</para>
///
/// <para><c>version()</c> rather than <c>SHOW crdb_version</c> or a <c>crdb_internal</c> lookup: it is
/// an ordinary backend function call, so it survives a transaction-pooling connection pooler that would
/// hide session state, it needs no privilege, and it answers on both engines instead of erroring on
/// one — an error is a worse signal than a string, because it cannot distinguish "wrong engine" from
/// "no permission".</para>
/// </remarks>
public static class DatabaseEngineProbe
{
    /// <summary>What the server on the other end of <paramref name="connection"/> actually is.</summary>
    public async static Task<DatabaseProviderKind> DetectAsync(DbConnection connection, CancellationToken ct = default)
    {
        await using var command = connection.CreateCommand();

        command.CommandText = "SELECT version()";

        var banner = await command.ExecuteScalarAsync(ct) as string ?? string.Empty;

        return banner.Contains("CockroachDB", StringComparison.OrdinalIgnoreCase)
            ? DatabaseProviderKind.CockroachDb
            : DatabaseProviderKind.PostgreSql;
    }

    /// <summary>
    /// Fails the boot when the declaration and the server disagree.
    /// </summary>
    /// <remarks>
    /// Loudly, and before migrations run, because both directions of the mismatch are bad in a way that
    /// is hard to diagnose later. Declaring Postgres on Cockroach silently skips the multi-region DDL
    /// and leaves a database nobody notices is unconfigured; declaring Cockroach on Postgres sends
    /// syntax that engine has never heard of, at whatever moment something first tries. A process that
    /// refuses to start names the wrong key in the message and costs one restart.
    /// </remarks>
    public async static Task VerifyAsync(
        DbConnection connection, DatabaseProviderKind declared, ILogger logger, CancellationToken ct = default)
    {
        var actual = await DetectAsync(connection, ct);

        if (actual == declared)
        {
            logger.LogInformation("Database engine is {Engine}, as configured", actual);
            return;
        }

        throw new InvalidOperationException(
            $"'{DatabaseFeature.ProviderConfigurationKey}' says {declared} but the server is {actual}. " +
            $"Set it to '{actual}' — note the enum members are 'CockroachDb' and 'PostgreSql', and that " +
            "an unset or misspelled value resolves to CockroachDb rather than failing.");
    }
}
