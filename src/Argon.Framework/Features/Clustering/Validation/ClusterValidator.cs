namespace Argon.Features.Clustering;

using Microsoft.AspNetCore.Mvc;

public sealed class ClusterValidationOptions
{
    /// <summary>
    /// Storage provider names registered by the core silo configuration. Empty disables E4 —
    /// a role set can be validated before the storage layer is wired up.
    /// </summary>
    public IReadOnlySet<string> StorageProviders { get; init; } = new HashSet<string>(StringComparer.Ordinal);

    /// <summary>Report grain classes that no role in the topology hosts (E2).</summary>
    public bool RequireEveryGrainHosted { get; init; } = true;

    /// <summary>Call-site count at which a cross-role edge is flagged as a hot-path risk (W2).</summary>
    public int HotEdgeThreshold { get; init; } = 4;

    /// <summary>
    /// Report grains hosted by exactly one role (W3). Off by default: replica counts live in the
    /// deployment manifests, not in code, so this check can only see role granularity and is noisy
    /// on a decomposition where most grains legitimately live in one place.
    /// </summary>
    public bool ReportSinglePointsOfFailure { get; init; }
}

/// <summary>What analysis found for one role inside a topology.</summary>
public sealed class RoleAnalysis
{
    public required RoleDescriptor  Role  { get; init; }
    public required GrainCallGraph  Graph { get; init; }

    /// <summary>Grain interfaces this role reaches, including those declared via <c>AddDynamicRef</c>.</summary>
    public required IReadOnlySet<Type> Calls { get; init; }

    /// <summary>Grain interfaces this role satisfies locally.</summary>
    public required IReadOnlySet<Type> Hosts { get; init; }
}

public sealed class ValidationReport
{
    public required string                                     Topology    { get; init; }
    public required IReadOnlyList<ClusterDiagnostic>           Diagnostics { get; init; }
    public required IReadOnlyDictionary<ArgonRoleId, RoleAnalysis> Roles   { get; init; }

    public IEnumerable<ClusterDiagnostic> Errors
        => Diagnostics.Where(d => d.Severity is ClusterDiagnosticSeverity.Error);

    public IEnumerable<ClusterDiagnostic> Warnings
        => Diagnostics.Where(d => d.Severity is ClusterDiagnosticSeverity.Warning);

    public bool IsValid
        => !Errors.Any();
}

/// <summary>
/// Checks that a topology is complete and coherent.
/// </summary>
/// <remarks>
/// <b>Errors.</b>
/// E1 a role calls a grain no role in the topology hosts;
/// E2 a grain class no role hosts at all;
/// E3 an <c>IRemindable</c> grain on a role without reminders;
/// E4 a hosted grain binding a storage provider the core configuration does not register;
/// E5 an <c>AddStartupCall</c> target not hosted locally;
/// E6 a client role declaring <c>AddToRef</c>;
/// E7 an unresolvable grain call site with no matching <c>AllowUnresolved</c>;
/// E8 a feature-graph cycle, missing dependency or conflict;
/// E9 a duplicate role id, an include cycle, or an unknown role in a topology;
/// E10 a topology with client roles and no silo role exposing a cluster gateway;
/// E11 an MVC controller no feature claims, which therefore no role routes.
/// <para>
/// <b>Warnings.</b>
/// W1 a role calls a <c>[StatelessWorker]</c> it does not host — the call works but loses worker
/// locality, so either host it or record the trade with <c>AcceptRemote</c>;
/// W2 an edge of weight ≥ <see cref="ClusterValidationOptions.HotEdgeThreshold"/> crossing a role
/// boundary, which is the rule that keeps a decomposition from silently drifting into the hot path;
/// W3 a grain hosted by exactly one role, off by default because replica counts live in the
/// deployment manifests rather than in code.
/// </para>
/// </remarks>
/// <remarks>
/// Each role is checked one hop deep. That is sufficient because every role in the topology is
/// checked: if role A calls a grain hosted by role B, B's own dependencies are covered by B's
/// validation, so the topology closes inductively without a transitive grain closure.
/// </remarks>
public static class ClusterValidator
{
    public static ValidationReport Validate(
        ArgonClusterCatalog        catalog,
        TopologyDescriptor         topology,
        GrainTypeIndex             index,
        IGrainGraphSource          graphSource,
        ClusterValidationOptions?  options = null)
    {
        options ??= new ClusterValidationOptions();

        var diagnostics = new List<ClusterDiagnostic>(catalog.Diagnostics);
        var analyses    = new Dictionary<ArgonRoleId, RoleAnalysis>();

        // ── resolve roles ───────────────────────────────────────────────────────────────────
        foreach (var roleId in topology.Roles)
        {
            if (catalog.Find(roleId) is not { } role)
            {
                diagnostics.Add(ClusterDiagnostic.Error("E9",
                    $"topology '{topology.Name}' references unknown role '{roleId}'", roleId));
                continue;
            }

            diagnostics.AddRange(role.Diagnostics);

            var roots = role.HostedGrains.Concat(role.CallRoots).Distinct().ToArray();
            var graph = roots.Length == 0 ? GrainCallGraph.Empty : graphSource.Analyze(roots);

            var calls = graph.All.ToHashSet();
            calls.UnionWith(role.DynamicRefs);
            calls.UnionWith(role.StartupCalls);

            analyses[roleId] = new RoleAnalysis
            {
                Role  = role,
                Graph = graph,
                Calls = calls,
                Hosts = index.InterfacesOf(role.HostedGrains)
            };
        }

        var hostedInterfaces = analyses.Values.SelectMany(a => a.Hosts).ToHashSet();
        var hostedClasses    = analyses.Values.SelectMany(a => a.Role.HostedGrains).ToHashSet();

        // Which role hosts a given grain interface — used by W1/W2 to name the other side.
        var hostedBy = new Dictionary<Type, List<ArgonRoleId>>();
        foreach (var (roleId, analysis) in analyses)
        foreach (var contract in analysis.Hosts)
        {
            if (!hostedBy.TryGetValue(contract, out var list))
                hostedBy[contract] = list = [];
            list.Add(roleId);
        }

        foreach (var (roleId, analysis) in analyses)
        {
            var role = analysis.Role;

            // ── E6: a client hosts nothing ──────────────────────────────────────────────────
            if (role.IsClient && role.HostedGrains.Count > 0)
                diagnostics.Add(ClusterDiagnostic.Error("E6",
                    $"client role '{roleId}' declares {role.HostedGrains.Count} hosted grain(s); " +
                    $"a client runs no silo and can host none", roleId));

            // ── E1: every call is satisfied somewhere in the topology ───────────────────────
            // Grain interfaces from outside the scanned assemblies are the runtime's to host —
            // Orleans' own IManagementGrain, for instance — so they are none of our business.
            foreach (var contract in analysis.Calls
                        .Where(c => index.IsOwned(c) && !hostedInterfaces.Contains(c))
                        .OrderBy(TypeName))
            {
                var message = index.Implementations(contract).Count == 0
                    ? $"calls '{TypeName(contract)}', which has no implementation in the scanned assemblies — dead interface"
                    : $"calls '{TypeName(contract)}', hosted by no role in topology '{topology.Name}'";
                diagnostics.Add(ClusterDiagnostic.Error("E1", $"role '{roleId}' {message}", roleId, TypeName(contract)));
            }

            // ── E5: startup calls must be satisfiable locally ───────────────────────────────
            foreach (var contract in analysis.Role.StartupCalls.Where(c => !analysis.Hosts.Contains(c)).OrderBy(TypeName))
                diagnostics.Add(ClusterDiagnostic.Error("E5",
                    $"role '{roleId}' invokes '{TypeName(contract)}' at startup but does not host it; " +
                    $"a startup task cannot rely on the rest of the cluster being up", roleId, TypeName(contract)));

            // ── E7: unresolvable dynamic dispatch ───────────────────────────────────────────
            foreach (var site in analysis.Graph.Unresolved
                        .Where(s => !role.AllowedUnresolved.ContainsKey(s.DeclaringType))
                        .DistinctBy(s => (s.DeclaringType, s.MethodName, s.Reason)))
                diagnostics.Add(ClusterDiagnostic.Error("E7",
                    $"role '{roleId}': {TypeName(site.DeclaringType)}.{site.MethodName} — {site.Reason}. " +
                    $"Declare AddDynamicRef<T>() for what it reaches and waive the site with " +
                    $"AllowUnresolved<{site.DeclaringType.Name}>(reason).", roleId, TypeName(site.DeclaringType)));

            foreach (var grainClass in role.HostedGrains.OrderBy(TypeName))
            {
                if (index.Info(grainClass) is not { } info)
                {
                    diagnostics.Add(ClusterDiagnostic.Error("E2",
                        $"role '{roleId}' hosts '{TypeName(grainClass)}', which is not a grain class",
                        roleId, TypeName(grainClass)));
                    continue;
                }

                // ── E3: reminders ───────────────────────────────────────────────────────────
                if (info.IsRemindable && !role.UsesReminders)
                    diagnostics.Add(ClusterDiagnostic.Error("E3",
                        $"role '{roleId}' hosts IRemindable grain '{TypeName(grainClass)}' but does not " +
                        $"configure reminders (set UsesReminders)", roleId, TypeName(grainClass)));

                // ── E4: storage providers ───────────────────────────────────────────────────
                if (options.StorageProviders.Count > 0)
                    foreach (var binding in info.StorageProviders)
                        if (binding.StorageName is { } provider && !options.StorageProviders.Contains(provider))
                            diagnostics.Add(ClusterDiagnostic.Error("E4",
                                $"role '{roleId}' hosts '{TypeName(grainClass)}' which binds storage provider " +
                                $"'{provider}', not registered by the core silo configuration",
                                roleId, TypeName(grainClass)));
            }

            if (role.IsClient)
                continue;   // W1 and W2 are about silo-local placement; a client activates nothing.

            // ── W1: stateless worker locality ───────────────────────────────────────────────
            foreach (var contract in analysis.Calls.Where(index.IsOwned).OrderBy(TypeName))
            {
                if (analysis.Hosts.Contains(contract) || role.AcceptedRemotes.ContainsKey(contract))
                    continue;
                if (!index.Implementations(contract).Any(c => index.Info(c)?.IsStatelessWorker is true))
                    continue;

                var owner = hostedBy.GetValueOrDefault(contract)?.FirstOrDefault();
                diagnostics.Add(ClusterDiagnostic.Warning("W1",
                    $"role '{roleId}' calls [StatelessWorker] '{TypeName(contract)}'" +
                    (owner is { } o ? $" hosted by '{o}'" : string.Empty) +
                    $"; the call works but loses worker locality. Host it here too, or record the trade " +
                    $"with AcceptRemote<{contract.Name}>(reason).", roleId, TypeName(contract)));
            }

            // ── W2: heavy edges crossing a role boundary ────────────────────────────────────
            foreach (var grainClass in role.HostedGrains.OrderBy(TypeName))
            foreach (var (contract, weight) in analysis.Graph.From(grainClass).Weights.OrderBy(p => TypeName(p.Key)))
            {
                if (weight < options.HotEdgeThreshold || analysis.Hosts.Contains(contract) || !index.IsOwned(contract))
                    continue;

                var owner = hostedBy.GetValueOrDefault(contract)?.FirstOrDefault();
                diagnostics.Add(ClusterDiagnostic.Warning("W2",
                    $"'{TypeName(grainClass)}' ({roleId}) calls '{TypeName(contract)}'" +
                    (owner is { } o ? $" ({o})" : string.Empty) +
                    $" from {weight} call sites — a heavy edge crossing a role boundary",
                    roleId, TypeName(grainClass)));
            }
        }

        // ── E2: orphan grains ───────────────────────────────────────────────────────────────
        if (options.RequireEveryGrainHosted)
            foreach (var grainClass in index.Classes.Keys.Where(c => !hostedClasses.Contains(c)).OrderBy(TypeName))
                diagnostics.Add(ClusterDiagnostic.Error("E2",
                    $"grain '{TypeName(grainClass)}' is hosted by no role in topology '{topology.Name}'",
                    target: TypeName(grainClass)));

        // ── E11: a controller nobody claims ─────────────────────────────────────────────────
        // Routing a controller is now a feature's decision, which means forgetting to declare one is
        // a controller that exists, compiles, and answers nowhere. That is the same silence the
        // filtering was introduced to remove, so it is refused here rather than discovered by a
        // client getting a 404 from an endpoint someone is sure they wrote.
        var claimedControllers = catalog.Roles.Values
           .SelectMany(role => role.Features.Ordered)
           .SelectMany(feature => feature.Controllers)
           .ToHashSet();

        foreach (var controller in catalog.Scope.Types()
                    .Where(t => t is { IsClass: true, IsAbstract: false, ContainsGenericParameters: false })
                    .Where(t => typeof(ControllerBase).IsAssignableFrom(t))
                    .Where(t => !claimedControllers.Contains(t))
                    .OrderBy(TypeName))
            diagnostics.Add(ClusterDiagnostic.Error("E11",
                $"controller '{TypeName(controller)}' is claimed by no feature, so no role routes it. " +
                "Declare it with Controller<T>() on the feature that owns its endpoints.",
                target: TypeName(controller)));

        // ── E10: somebody has to accept client connections ──────────────────────────────────
        // An Orleans client reaches grains only through a gateway, and a silo configured with proxy
        // port 0 is never one — every gateway list provider filters on ProxyPort != 0. A topology
        // with client roles and no gateway therefore starts clean and answers nothing, which is how
        // this shipped: ExposesClusterGateway defaults to false and no silo role overrode it.
        var clientRoles = analyses.Values.Where(a => a.Role.IsClient).Select(a => a.Role.Id).ToArray();
        var gateways    = analyses.Values.Where(a => !a.Role.IsClient && a.Role.ExposesClusterGateway)
           .Select(a => a.Role.Id).ToArray();

        if (clientRoles.Length > 0 && gateways.Length == 0)
            diagnostics.Add(ClusterDiagnostic.Error("E10",
                $"topology '{topology.Name}' has client role(s) {string.Join(", ", clientRoles)} and no silo " +
                "role exposing a cluster gateway; a client has nothing to connect to. Set " +
                "ExposesClusterGateway on the role clients should reach the cluster through."));

        // ── W3: single point of failure ─────────────────────────────────────────────────────
        if (options.ReportSinglePointsOfFailure)
            foreach (var (contract, owners) in hostedBy.Where(p => p.Value.Count == 1).OrderBy(p => TypeName(p.Key)))
                diagnostics.Add(ClusterDiagnostic.Warning("W3",
                    $"'{TypeName(contract)}' is hosted only by role '{owners[0]}'",
                    owners[0], TypeName(contract)));

        return new ValidationReport
        {
            Topology    = topology.Name,
            Diagnostics = diagnostics,
            Roles       = analyses
        };
    }

    private static string TypeName(Type type)
        => type.Name;

    private static string TypeName(KeyValuePair<Type, List<ArgonRoleId>> pair)
        => pair.Key.Name;
}
