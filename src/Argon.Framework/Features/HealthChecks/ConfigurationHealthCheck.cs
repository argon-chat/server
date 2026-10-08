namespace Argon.HealthChecks;

using Argon.Features.Clustering;
using Microsoft.Extensions.Diagnostics.HealthChecks;

/// <summary>
/// What the configuration validator had to say about this role when the process started.
/// </summary>
/// <remarks>
/// Only the warnings. An error ends the process before anything is registered, which is the
/// validator's job and the same job it does in the <c>--validate-config</c> sidecar; what a running
/// role has left to report is the findings it was allowed to start with.
/// </remarks>
public sealed record RoleConfigurationVerdict(
    ArgonRoleId                      Role,
    IReadOnlyList<ClusterDiagnostic> Warnings,
    DateTimeOffset                   CheckedAt);

/// <summary>
/// The validator's warnings as a health check entry, for <c>/health</c>.
/// </summary>
/// <remarks>
/// A diagnostic, never a probe: a warning is by definition something the role can run with, so the
/// entry is healthy whatever it carries. It exists because the warnings are otherwise gone the moment
/// they are printed, and "is this pod running the configuration you think" is the first question
/// asked of one that is behaving oddly.
/// </remarks>
public sealed class ConfigurationHealthCheck(RoleConfigurationVerdict verdict) : IHealthCheck
{
    public const string Name = "configuration";

    public Task<HealthCheckResult> CheckHealthAsync(HealthCheckContext context, CancellationToken cancellationToken = default)
    {
        var warnings = verdict.Warnings.Select(d => d.ToString()).ToArray();

        return Task.FromResult(HealthCheckResult.Healthy(
            warnings.Length == 0
                ? "configuration validated at start-up"
                : $"configuration validated at start-up with {warnings.Length} warning(s)",
            new Dictionary<string, object>
            {
                ["role"]      = verdict.Role.Value,
                ["checkedAt"] = verdict.CheckedAt.ToString("O"),
                ["warnings"]  = warnings
            }));
    }
}
