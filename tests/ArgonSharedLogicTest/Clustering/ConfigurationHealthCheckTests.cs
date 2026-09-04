namespace ArgonSharedLogicTest.Clustering;

using Argon.Features.Clustering;
using Argon.HealthChecks;
using Microsoft.Extensions.Diagnostics.HealthChecks;

/// <summary>
/// The validator's warnings, kept where <c>/health</c> can show them.
/// </summary>
[TestFixture]
public class ConfigurationHealthCheckTests
{
    private static ArgonRoleId Core => new("core");

    private static readonly ClusterDiagnostic Warning =
        ClusterDiagnostic.Warning("C7", "conf.d/aegis.json names no feature this role enables", Core);

    /// <summary>A running role passed validation; a warning is something it can run with.</summary>
    [Test]
    public async Task A_running_role_reports_its_warnings_and_stays_healthy()
    {
        var check  = new ConfigurationHealthCheck(new RoleConfigurationVerdict(Core, [Warning], DateTimeOffset.UtcNow));
        var result = await check.CheckHealthAsync(new HealthCheckContext());

        Assert.Multiple(() =>
        {
            Assert.That(result.Status, Is.EqualTo(HealthStatus.Healthy));
            Assert.That(result.Description, Does.Contain("1 warning"));
            Assert.That(result.Data["warnings"], Is.EqualTo(new[] { Warning.ToString() }));
            Assert.That(result.Data["role"], Is.EqualTo("core"));
        });
    }

    [Test]
    public async Task A_clean_configuration_says_so()
    {
        var check  = new ConfigurationHealthCheck(new RoleConfigurationVerdict(Core, [], DateTimeOffset.UtcNow));
        var result = await check.CheckHealthAsync(new HealthCheckContext());

        Assert.Multiple(() =>
        {
            Assert.That(result.Status, Is.EqualTo(HealthStatus.Healthy));
            Assert.That(result.Description, Is.EqualTo("configuration validated at start-up"));
            Assert.That(result.Data["warnings"], Is.Empty);
        });
    }
}
