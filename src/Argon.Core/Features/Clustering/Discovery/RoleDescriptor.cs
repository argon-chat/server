namespace Argon.Features.Clustering;

/// <summary>
/// A role after discovery and composition: what it hosts, what it roots analysis at, and which
/// features it runs, with <see cref="Include{TRole}"/> already flattened.
/// </summary>
public sealed class RoleDescriptor
{
    public required ArgonRoleId Id                    { get; init; }
    public required Type        RoleType              { get; init; }
    public required bool        IsClient              { get; init; }
    public required bool        ExposesClusterGateway { get; init; }
    public required bool        UsesReminders         { get; init; }
    public          string      Description           { get; init; } = string.Empty;

    /// <summary>Grain classes this role activates. Feeds <c>GrainTypeOptions.Classes</c>.</summary>
    public required IReadOnlySet<Type> HostedGrains { get; init; }

    public IReadOnlySet<Type> CallRoots => callRoots.Value;

    public required Lazy<IReadOnlySet<Type>> LazyCallRoots
    {
        get => callRoots;
        init => callRoots = value;
    }

    private readonly Lazy<IReadOnlySet<Type>> callRoots = new(() => new HashSet<Type>());

    /// <summary>Grain interfaces declared via <c>AddDynamicRef</c>, invisible to static analysis.</summary>
    public required IReadOnlySet<Type> DynamicRefs { get; init; }

    /// <summary>Grain interfaces invoked at startup, which must therefore be hosted locally (E5).</summary>
    public required IReadOnlySet<Type> StartupCalls { get; init; }

    /// <summary>Grain interfaces whose remote hop was explicitly accepted, keyed to the reason.</summary>
    public required IReadOnlyDictionary<Type, string> AcceptedRemotes { get; init; }

    /// <summary>Types whose unresolvable call sites were reviewed and waived, keyed to the reason.</summary>
    public required IReadOnlyDictionary<Type, string> AllowedUnresolved { get; init; }

    /// <summary>Roles flattened into this one, in include order. Empty for a leaf role.</summary>
    public required IReadOnlyList<Type> IncludedRoles { get; init; }

    public required FeatureGraph Features { get; init; }

    /// <summary>Problems found while composing the role itself, before topology validation.</summary>
    public required IReadOnlyList<ClusterDiagnostic> Diagnostics { get; init; }

    public override string ToString()
        => $"{Id} ({(IsClient ? "client" : "silo")}, {HostedGrains.Count} grains, {Features.Ordered.Count} features)";
}
