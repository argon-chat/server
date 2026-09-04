namespace ArgonSharedLogicTest.Clustering;

using Argon.Features.Clustering;
using Argon.HealthChecks;

/// <summary>
/// The <c>Probes</c> section: what it defaults to, what it refuses, and what it merely doubts.
/// </summary>
[TestFixture]
public class ProbeOptionsRulesTests
{
    private const string Section = ProbeOptionsFeature.Section;

    private static FeatureConfigurationReportSet Validate(params (string, string?)[] values)
        => FeatureConfigurationValidator.Validate(
            ConfigurationFixtures.Role<ProbeOptionsRole>(), ConfigurationFixtures.From(values));

    private static ProbeOptions Bind(params (string, string?)[] values)
        => FeatureCatalog.Describe<ProbeOptionsFeature>().BindOptions<ProbeOptions>(ConfigurationFixtures.From(values));

    [Test]
    public void The_shipped_defaults_gate_startup_report_on_readiness_and_leave_liveness_alone()
    {
        var options = Bind();
        var report  = Validate();

        Assert.Multiple(() =>
        {
            Assert.That(options.Dependencies.Startup, Is.EqualTo(ProbeGate.Fail));
            Assert.That(options.Dependencies.Readiness, Is.EqualTo(ProbeGate.Degrade));
            Assert.That(options.Dependencies.Liveness, Is.EqualTo(ProbeGate.Off));
            Assert.That(options.Dependencies.Timeout, Is.EqualTo(TimeSpan.FromSeconds(5)));
            Assert.That(report.Diagnostics, Is.Empty, "the defaults must validate clean on every role");
        });
    }

    /// <summary>
    /// Longer than the probe period is a check Kubernetes has stopped waiting for; shorter than a
    /// second is a check that fails on a busy network for nothing.
    /// </summary>
    [Test]
    public void A_timeout_outside_a_second_and_a_minute_is_rejected()
    {
        var report = Validate(($"{Section}:Dependencies:Timeout", "00:05:00"));

        Assert.Multiple(() =>
        {
            Assert.That(report.IsValid, Is.False);
            Assert.That(report.Errors.Select(e => e.Target), Does.Contain($"{Section}:dependencies:Timeout"));
        });
    }

    /// <summary>A typo in an override applies to nothing, silently, which is worse than an error.</summary>
    [Test]
    public void An_override_naming_no_check_is_a_warning()
    {
        var report = Validate(($"{Section}:Dependencies:Overrides:vualt:Startup", "Degrade"));

        Assert.Multiple(() =>
        {
            Assert.That(report.IsValid, Is.True, "a doubtful override must not stop a role from starting");
            Assert.That(report.Warnings.Select(w => w.Target), Does.Contain($"{Section}:dependencies:overrides:vualt"));
        });
    }

    [Test]
    public void An_override_binds_and_applies_to_the_probe_it_names()
    {
        var options = Bind(($"{Section}:Dependencies:Overrides:vault:Startup", "Degrade")).Dependencies;

        Assert.Multiple(() =>
        {
            Assert.That(options.GateFor(ProbeKind.Startup, DependencyNames.Vault), Is.EqualTo(ProbeGate.Degrade));
            Assert.That(options.GateFor(ProbeKind.Readiness, DependencyNames.Vault), Is.EqualTo(ProbeGate.Degrade),
                "the members an override leaves unset fall back to the section's own");
            Assert.That(options.GateFor(ProbeKind.Startup, DependencyNames.Database), Is.EqualTo(ProbeGate.Fail),
                "an override is about the one dependency it names");
        });
    }
}
