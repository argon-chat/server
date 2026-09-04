namespace ArgonSharedLogicTest;

using Argon.HealthChecks;
using Microsoft.Extensions.Diagnostics.HealthChecks;

/// <summary>
/// Which checks each probe runs, and what their results add up to.
/// </summary>
/// <remarks>
/// The probes are what a deployment is judged by, so the answers that matter are the ones that
/// keep a pod out — startup failing on a dependency it cannot reach — and the ones that must not
/// take a pod out: readiness reporting a shared outage without removing every pod from every Service
/// at once.
/// </remarks>
[TestFixture]
public class ProbePolicyTests
{
    private const string Dependency = DependencyHealthCheckExtensions.Tag;

    private sealed class Never : IHealthCheck
    {
        public Task<HealthCheckResult> CheckHealthAsync(HealthCheckContext context, CancellationToken cancellationToken = default)
            => throw new NotSupportedException("the policy is under test, not the check");
    }

    private static HealthCheckRegistration Registration(string name, params string[] tags)
        => new(name, new Never(), null, tags);

    private static HealthCheckRegistration DependencyRegistration(string name)
        => Registration(name, Dependency, name);

    private static HealthReportEntry Entry(HealthStatus status, params string[] tags)
        => new(status, null, TimeSpan.Zero, null, null, tags);

    private static (string, HealthReportEntry) Own(ProbeKind probe, HealthStatus status)
        => (ProbePolicy.TagOf(probe), Entry(status, ProbePolicy.TagOf(probe)));

    private static (string, HealthReportEntry) Dependent(string name, HealthStatus status)
        => (name, Entry(status, Dependency, name));

    private static HealthReport Report(params (string Name, HealthReportEntry Entry)[] entries)
        => new(entries.ToDictionary(e => e.Name, e => e.Entry), TimeSpan.Zero);

    private static DependencyProbeOptions Defaults() => new ProbeOptions().Dependencies;

    // ── which checks a probe runs ───────────────────────────────────────────────────────────

    [Test]
    public void Startup_and_readiness_run_the_dependency_checks_and_liveness_does_not()
    {
        var database = DependencyRegistration(DependencyNames.Database);
        var policy   = Defaults();

        Assert.Multiple(() =>
        {
            Assert.That(ProbePolicy.Includes(database, ProbeKind.Startup, policy), Is.True);
            Assert.That(ProbePolicy.Includes(database, ProbeKind.Readiness, policy), Is.True);

            // A restart is the only remedy liveness has, and it does not bring a database back.
            Assert.That(ProbePolicy.Includes(database, ProbeKind.Liveness, policy), Is.False);
        });
    }

    [Test]
    public void A_probes_own_check_runs_whatever_the_policy_says()
    {
        var liveness = Registration("liveness", "live", "liveness");
        var policy   = new DependencyProbeOptions
        {
            Startup   = ProbeGate.Off,
            Readiness = ProbeGate.Off,
            Liveness  = ProbeGate.Off
        };

        Assert.Multiple(() =>
        {
            Assert.That(ProbePolicy.Includes(liveness, ProbeKind.Liveness, policy), Is.True);
            Assert.That(ProbePolicy.Includes(liveness, ProbeKind.Readiness, policy), Is.False,
                "a check tagged for one probe does not answer another");
        });
    }

    [Test]
    public void An_override_can_keep_one_dependency_off_a_probe()
    {
        var policy = Defaults();
        policy.Overrides[DependencyNames.Vault] = new DependencyGateOverride { Startup = ProbeGate.Off };

        var vault    = DependencyRegistration(DependencyNames.Vault);
        var database = DependencyRegistration(DependencyNames.Database);

        Assert.Multiple(() =>
        {
            Assert.That(ProbePolicy.Includes(vault, ProbeKind.Startup, policy), Is.False);
            Assert.That(ProbePolicy.Includes(database, ProbeKind.Startup, policy), Is.True);
            Assert.That(ProbePolicy.Includes(vault, ProbeKind.Readiness, policy), Is.True,
                "an override sets only the members it names; the rest fall back to the defaults");
        });
    }

    [Test]
    public void An_override_is_matched_whatever_its_case()
    {
        var policy = Defaults();
        policy.Overrides["VAULT"] = new DependencyGateOverride { Startup = ProbeGate.Degrade };

        Assert.That(policy.GateFor(ProbeKind.Startup, DependencyNames.Vault), Is.EqualTo(ProbeGate.Degrade));
    }

    // ── what the results add up to ──────────────────────────────────────────────────────────

    /// <summary>
    /// The gate a blue/green deployment turns on: a pod that cannot reach what it needs never
    /// passes startup, so the rollout never promotes it.
    /// </summary>
    [Test]
    public void Startup_fails_on_a_dependency_it_cannot_reach()
    {
        var report = Report(Own(ProbeKind.Startup, HealthStatus.Healthy), Dependent(DependencyNames.Database, HealthStatus.Unhealthy));

        Assert.That(ProbePolicy.Judge(report, ProbeKind.Startup, Defaults()).Status, Is.EqualTo(HealthStatus.Unhealthy));
    }

    /// <summary>
    /// Every pod of every role shares the database and the Redis, so failing readiness on them
    /// would take all of them out of their Services together. Reported, not removed.
    /// </summary>
    [Test]
    public void Readiness_reports_an_unreachable_dependency_and_keeps_the_pod()
    {
        var report = Report(Own(ProbeKind.Readiness, HealthStatus.Healthy), Dependent(DependencyNames.Redis, HealthStatus.Unhealthy));
        var judged = ProbePolicy.Judge(report, ProbeKind.Readiness, Defaults());

        Assert.Multiple(() =>
        {
            Assert.That(judged.Status, Is.EqualTo(HealthStatus.Degraded));
            Assert.That(judged.Entries[DependencyNames.Redis].Status, Is.EqualTo(HealthStatus.Unhealthy),
                "the entry keeps its own verdict; only the aggregate a probe answers with is softened");
        });
    }

    [Test]
    public void The_probes_own_check_is_never_softened()
    {
        var report = Report(Own(ProbeKind.Readiness, HealthStatus.Unhealthy), Dependent(DependencyNames.Redis, HealthStatus.Healthy));

        Assert.That(ProbePolicy.Judge(report, ProbeKind.Readiness, Defaults()).Status, Is.EqualTo(HealthStatus.Unhealthy),
            "a draining silo says not-ready and means it");
    }

    [Test]
    public void Readiness_can_be_told_to_fail_instead()
    {
        var policy = new DependencyProbeOptions { Readiness = ProbeGate.Fail };
        var report = Report(Own(ProbeKind.Readiness, HealthStatus.Healthy), Dependent(DependencyNames.Database, HealthStatus.Unhealthy));

        Assert.That(ProbePolicy.Judge(report, ProbeKind.Readiness, policy).Status, Is.EqualTo(HealthStatus.Unhealthy));
    }

    [Test]
    public void An_override_softens_one_dependency_and_not_the_rest()
    {
        var policy = Defaults();
        policy.Overrides[DependencyNames.Vault] = new DependencyGateOverride { Startup = ProbeGate.Degrade };

        var vaultOnly = Report(Own(ProbeKind.Startup, HealthStatus.Healthy), Dependent(DependencyNames.Vault, HealthStatus.Unhealthy));
        var both      = Report(Own(ProbeKind.Startup, HealthStatus.Healthy),
            Dependent(DependencyNames.Vault, HealthStatus.Unhealthy),
            Dependent(DependencyNames.Nats, HealthStatus.Unhealthy));

        Assert.Multiple(() =>
        {
            Assert.That(ProbePolicy.Judge(vaultOnly, ProbeKind.Startup, policy).Status, Is.EqualTo(HealthStatus.Degraded));
            Assert.That(ProbePolicy.Judge(both, ProbeKind.Startup, policy).Status, Is.EqualTo(HealthStatus.Unhealthy));
        });
    }

    [Test]
    public void A_report_with_nothing_wrong_is_healthy()
    {
        var report = Report(Own(ProbeKind.Startup, HealthStatus.Healthy), Dependent(DependencyNames.Nats, HealthStatus.Healthy));
        var judged = ProbePolicy.Judge(report, ProbeKind.Startup, Defaults());

        Assert.Multiple(() =>
        {
            Assert.That(judged.Status, Is.EqualTo(HealthStatus.Healthy));
            Assert.That(judged.Entries, Has.Count.EqualTo(2), "the entries pass through for /health to show");
        });
    }
}
