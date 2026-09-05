namespace Argon.HealthChecks;

using Argon.Features.Aegis;
using Microsoft.Extensions.Diagnostics.HealthChecks;

/// <summary>
/// Can the identity server reach the database its key ring is kept in?
/// </summary>
/// <remarks>
/// <para>The same round trip as <see cref="DatabaseHealthCheck"/> — a connection and one
/// <c>SELECT 1</c> — over <see cref="AegisKeyRingDbContext"/>, which is the only context the
/// identity server has. Its own check rather than that one because the two contexts are registered
/// by different features and the co-hosted role enables both, and because the failure it catches is
/// its own: a role that cannot read its ring answers every cookie with a sign-out.</para>
///
/// <para>The context is scoped, so the probe opens a scope of its own rather than taking the context
/// in the constructor; disposing the scope is what returns the connection, whatever the outcome.</para>
/// </remarks>
public sealed class KeyRingHealthCheck(IServiceScopeFactory scopes, IOptions<ProbeOptions> options)
    : DependencyHealthCheck(options)
{
    protected override async Task<HealthCheckResult> ProbeAsync(CancellationToken ct)
    {
        using var scope = scopes.CreateScope();

        var db = scope.ServiceProvider.GetRequiredService<AegisKeyRingDbContext>();

        await db.Database.OpenConnectionAsync(ct);

        var connection = db.Database.GetDbConnection();

        await using var command = connection.CreateCommand();
        command.CommandText = "SELECT 1";
        await command.ExecuteScalarAsync(ct);

        return HealthCheckResult.Healthy($"key ring at {connection.DataSource} answered", new Dictionary<string, object>
        {
            ["server"]        = connection.DataSource,
            ["database"]      = connection.Database,
            ["serverVersion"] = connection.ServerVersion
        });
    }
}
