namespace Argon.HealthChecks;

using Argon.Features.Clustering;

/// <summary>
/// What the Kubernetes probes gate on besides the process's own state.
/// </summary>
/// <remarks>
/// <para>The probes used to answer from the process alone: has the silo joined, is it draining, has
/// the client reached a gateway. A process can pass all of that and still be unable to serve — the
/// database connection string points at the old cluster, the NATS URL is the other region's, a
/// NetworkPolicy stops it reaching Redis. Under a rolling update that shows up as errors on the
/// pods that were just promoted; under a blue/green deployment it shows up as the new colour taking
/// the traffic and failing every request, because nothing the probes looked at was wrong.</para>
///
/// <para>So every feature that opens a connection to something outside the process now contributes a
/// check, and this section says which probes those checks may fail. The three probes are asked
/// separately because Kubernetes acts on them differently, and the defaults follow from what each
/// action costs:</para>
/// <list type="bullet">
/// <item><b>startup</b> — <see cref="ProbeGate.Fail"/>. It is asked until it passes once, and a pod it
/// never passes on is one Kubernetes never routes to and a rollout never promotes. That is exactly
/// the gate a deployment wants: a pod that cannot reach what it needs stays out, the old colour keeps
/// serving, and the rollout is rolled back or held for someone to look at.</item>
/// <item><b>readiness</b> — <see cref="ProbeGate.Degrade"/>. Failing it takes a pod out of its
/// Service, and every pod of every role shares the same database and the same Redis. Failing all of
/// them at once on a shared outage turns a partial failure into a total one, which is the same
/// reasoning that keeps a client role's readiness from following the gateway count. The failure is
/// still reported — as <c>Degraded</c>, which a person sees on the endpoint and Kubernetes reads as
/// ready — and a deployment that would rather remove such pods sets this to <c>Fail</c>.</item>
/// <item><b>liveness</b> — <see cref="ProbeGate.Off"/>. Its only remedy is a restart, and a restart
/// does not bring a database back. A pod restarted for a dependency's outage returns to the same
/// outage minus the connections it was holding.</item>
/// </list>
///
/// <para>One dependency can be gated differently from the rest through <c>Overrides</c>, keyed by the
/// check's name: <c>vault</c> is the usual candidate, since only operator step-up reads it.</para>
/// </remarks>
public sealed class ProbeOptions : IValidatableFeatureOptions
{
    public const string SectionName = "Probes";

    public DependencyProbeOptions Dependencies { get; set; } = new();

    public void Validate(IFeatureConfigurationReport report)
    {
        report.RequireRange(Dependencies.Timeout, TimeSpan.FromSeconds(1), TimeSpan.FromMinutes(1),
            $"dependencies:{nameof(Dependencies.Timeout)}");

        // A typo here is silent otherwise: the override applies to nothing and the dependency keeps
        // its default gate, which is the opposite of what the person writing it wanted.
        foreach (var name in Dependencies.Overrides.Keys)
            report.Prefer(DependencyNames.All.Contains(name, StringComparer.OrdinalIgnoreCase),
                $"dependencies:overrides:{name}",
                $"names no dependency check; the checks are {string.Join(", ", DependencyNames.All)}");
    }
}

/// <summary>The three probes a manifest wires, as the thing a policy is keyed by.</summary>
public enum ProbeKind
{
    Startup,
    Readiness,
    Liveness
}

/// <summary>What a failed dependency check does to one probe.</summary>
public enum ProbeGate
{
    /// <summary>The probe does not run the check.</summary>
    Off,

    /// <summary>
    /// The probe runs the check and reports a failure as <c>Degraded</c>: visible on the endpoint,
    /// still <c>200</c> to Kubernetes.
    /// </summary>
    Degrade,

    /// <summary>The probe runs the check and a failure fails the probe.</summary>
    Fail
}

/// <summary>How each probe treats the dependency checks, and how long a check may take.</summary>
public sealed class DependencyProbeOptions
{
    public ProbeGate Startup   { get; set; } = ProbeGate.Fail;
    public ProbeGate Readiness { get; set; } = ProbeGate.Degrade;
    public ProbeGate Liveness  { get; set; } = ProbeGate.Off;

    /// <summary>
    /// The most a single dependency check may take before it is reported as failed.
    /// </summary>
    /// <remarks>
    /// Shorter than the clients' own connect timeouts on purpose — NATS is given a minute to connect
    /// and Npgsql fifteen seconds — because a probe that waits that long is answered after Kubernetes
    /// has already given up on it. Five seconds is the shipped probe period.
    /// </remarks>
    public TimeSpan Timeout { get; set; } = TimeSpan.FromSeconds(5);

    /// <summary>Per-dependency gates, keyed by check name. Unset members fall back to the three above.</summary>
    public Dictionary<string, DependencyGateOverride> Overrides { get; set; } = new(StringComparer.OrdinalIgnoreCase);

    public ProbeGate GateFor(ProbeKind probe, string dependency)
    {
        // A scan rather than a lookup: the binder may replace the dictionary with one of its own,
        // and with it the comparer, and a key that matches only by case would then apply to nothing.
        var overridden = Overrides
           .FirstOrDefault(pair => string.Equals(pair.Key, dependency, StringComparison.OrdinalIgnoreCase))
           .Value;

        return probe switch
        {
            ProbeKind.Startup   => overridden?.Startup ?? Startup,
            ProbeKind.Readiness => overridden?.Readiness ?? Readiness,
            ProbeKind.Liveness  => overridden?.Liveness ?? Liveness,
            _                   => ProbeGate.Off
        };
    }
}

public sealed class DependencyGateOverride
{
    public ProbeGate? Startup   { get; set; }
    public ProbeGate? Readiness { get; set; }
    public ProbeGate? Liveness  { get; set; }
}

/// <summary>
/// The names the dependency checks are registered under — what <c>Overrides</c> is keyed by and
/// what <c>/health</c> lists.
/// </summary>
public static class DependencyNames
{
    public const string Database      = "database";
    public const string Nats          = "nats";
    public const string Redis         = "redis";
    public const string ObjectStorage = "object-storage";
    public const string Vault         = "vault";
    public const string Sfu           = "sfu";

    public static readonly IReadOnlyList<string> All = [Database, Nats, Redis, ObjectStorage, Vault, Sfu];
}
