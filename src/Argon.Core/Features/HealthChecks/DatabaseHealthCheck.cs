namespace Argon.HealthChecks;

using Argon.Features.EF;
using Microsoft.Extensions.Diagnostics.HealthChecks;

/// <summary>
/// Can this process reach the database it is configured for?
/// </summary>
/// <remarks>
/// <para>A connection from the pool and one <c>SELECT 1</c> over it. That is the whole of it, and it
/// is enough: the failures this exists to catch are a connection string pointing at the wrong
/// cluster, credentials the new deployment does not have, and a network path that is not open —
/// none of which need a query to show.</para>
///
/// <para>The raw connection rather than EF's <c>ExecuteSqlRaw</c>, because the context is configured
/// to retry on failure — five attempts, up to two seconds apart — and a probe that retries is a probe
/// that answers late. The connection is opened through the context so that disposing the context
/// returns it, whatever the outcome.</para>
///
/// <para>Silos migrate at start-up and would not be here with a schema they disagree with; client
/// roles do not migrate and read whatever schema is there. Neither is checked, deliberately: whether
/// the schema is current is a question about the deployment, and the answer would take this pod out
/// for something a restart cannot fix.</para>
/// </remarks>
public sealed class DatabaseHealthCheck(
    IDbContextFactory<ApplicationDbContext> factory,
    DatabaseProvider                         provider,
    IOptions<ProbeOptions>                   options) : DependencyHealthCheck(options)
{
    protected override async Task<HealthCheckResult> ProbeAsync(CancellationToken ct)
    {
        await using var db = await factory.CreateDbContextAsync(ct);

        await db.Database.OpenConnectionAsync(ct);

        var connection = db.Database.GetDbConnection();

        await using var command = connection.CreateCommand();
        command.CommandText = "SELECT 1";
        await command.ExecuteScalarAsync(ct);

        return HealthCheckResult.Healthy($"{provider.Kind} at {connection.DataSource} answered", new Dictionary<string, object>
        {
            ["engine"]        = provider.Kind.ToString(),
            ["server"]        = connection.DataSource,
            ["database"]      = connection.Database,
            ["serverVersion"] = connection.ServerVersion
        });
    }
}
