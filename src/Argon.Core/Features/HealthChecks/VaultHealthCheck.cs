namespace Argon.HealthChecks;

using Microsoft.Extensions.Diagnostics.HealthChecks;
using VaultSharp;

/// <summary>
/// Is Vault reachable, initialised and unsealed?
/// </summary>
/// <remarks>
/// <para><c>sys/health</c>, which needs no token and answers all three in one call. A sealed Vault is
/// the case worth naming: it is the state a Vault comes up in after a restart, everything that reads
/// a secret fails against it, and nothing in this process would notice until an operator presented a
/// smart card — <c>VaultPkiService</c> resolves the client inside the call. The probe notices at
/// start-up instead.</para>
///
/// <para>Registered only where a client is: the vault feature resolves <c>None</c> when nothing names
/// a Vault, and a deployment without one has nothing here to be unhealthy about.</para>
/// </remarks>
public sealed class VaultHealthCheck(IVaultClient vault, IOptions<ProbeOptions> options)
    : DependencyHealthCheck(options)
{
    protected override async Task<HealthCheckResult> ProbeAsync(CancellationToken ct)
    {
        var health = await vault.V1.System.GetHealthStatusAsync();

        var data = new Dictionary<string, object>
        {
            ["initialized"] = health.Initialized,
            ["sealed"]      = health.Sealed,
            ["standby"]     = health.Standby,
            ["version"]     = health.Version ?? "?",
            ["cluster"]     = health.ClusterName ?? "?"
        };

        if (!health.Initialized)
            return HealthCheckResult.Unhealthy("Vault is not initialised", data: data);

        if (health.Sealed)
            return HealthCheckResult.Unhealthy("Vault is sealed", data: data);

        return HealthCheckResult.Healthy(
            $"Vault {health.Version} is unsealed{(health.Standby ? " (standby)" : string.Empty)}", data);
    }
}
