namespace Argon.Features.Clustering;

/// <summary>
/// Reflection-based discovery of roles, features and topologies, plus role composition.
/// </summary>
public sealed class ArgonClusterCatalog
{
    private readonly Dictionary<ArgonRoleId, RoleDescriptor> roles      = [];
    private readonly Dictionary<string, TopologyDescriptor>  topologies = new(StringComparer.OrdinalIgnoreCase);
    private readonly List<ClusterDiagnostic>                 diagnostics = [];

    private ArgonClusterCatalog(ClusterScanScope scope)
        => Scope = scope;

    public ClusterScanScope                                 Scope       { get; }
    public IReadOnlyList<Assembly>                          Assemblies  => Scope.Assemblies;
    public IReadOnlyDictionary<ArgonRoleId, RoleDescriptor> Roles       => roles;
    public IReadOnlyDictionary<string, TopologyDescriptor>  Topologies  => topologies;
    public IReadOnlyList<ClusterDiagnostic>                 Diagnostics => diagnostics;

    public static ArgonClusterCatalog Build(ClusterScanScope? scope = null)
    {
        var catalog = new ArgonClusterCatalog(scope ?? ClusterScanScope.Default());
        catalog.DiscoverRoles();
        catalog.DiscoverTopologies();
        return catalog;
    }

    public RoleDescriptor? Find(ArgonRoleId id)
        => roles.GetValueOrDefault(id);

    public RoleDescriptor Require(ArgonRoleId id)
        => roles.GetValueOrDefault(id)
        ?? throw new InvalidOperationException(
               $"Unknown role '{id}'. Known roles: {string.Join(", ", roles.Keys.Select(x => x.Value).Order())}");

    private void DiscoverRoles()
    {
        var roleTypes = ConcreteImplementationsOf(typeof(IArgonRole));

        foreach (var roleType in roleTypes)
        {
            if (!TryReadRoleId(roleType, out var id))
                continue;

            if (roles.TryGetValue(id, out var existing))
            {
                diagnostics.Add(ClusterDiagnostic.Error("E9",
                    $"duplicate role id '{id}' declared by '{existing.RoleType.FullName}' and '{roleType.FullName}'",
                    id, roleType.FullName));
                continue;
            }

            roles[id] = Compose(id, roleType);
        }
    }

    private bool TryReadRoleId(Type roleType, out ArgonRoleId id)
    {
        id = default;

        var property = roleType.GetProperty(nameof(IArgonRole.Id),
            BindingFlags.Public | BindingFlags.Static | BindingFlags.FlattenHierarchy);

        if (property?.GetValue(null) is not ArgonRoleId value)
        {
            diagnostics.Add(ClusterDiagnostic.Error("E9",
                $"role '{roleType.FullName}' does not expose a public static ArgonRoleId Id. " +
                $"Implement it as a public static property, not an explicit interface implementation.",
                target: roleType.FullName));
            return false;
        }

        if (value.IsEmpty)
        {
            diagnostics.Add(ClusterDiagnostic.Error("E9",
                $"role '{roleType.FullName}' declares an empty id", target: roleType.FullName));
            return false;
        }

        id = value;
        return true;
    }

    /// <summary>
    /// Flattens the include graph. Only the root role contributes <see cref="IArgonRole.IsClient"/>
    /// and friends; included roles contribute grains, call roots and features only.
    /// </summary>
    private RoleDescriptor Compose(ArgonRoleId id, Type roleType)
    {
        var roleDiagnostics = new List<ClusterDiagnostic>();
        var grains      = new GrainCollectionRegistry();
        var features    = new ArgonFeatureRegistry();
        var included    = new List<Type>();
        var visited     = new HashSet<Type>();
        var root        = (IArgonRole)Activator.CreateInstance(roleType)!;

        void Walk(Type current, IArgonRole instance, Stack<Type> path)
        {
            if (!visited.Add(current))
                return;

            var local = new GrainCollectionRegistry();
            instance.OnGrainReferences(local);
            instance.OnFeatures(features);
            local.MergeInto(grains);

            foreach (var includedType in local.Includes)
            {
                if (path.Contains(includedType))
                {
                    var cycle = string.Join(" -> ", path.Reverse().Select(t => t.Name).Append(includedType.Name));
                    roleDiagnostics.Add(ClusterDiagnostic.Error("E9",
                        $"role include cycle: {cycle}", id, includedType.FullName));
                    continue;
                }

                if (visited.Contains(includedType))
                    continue;

                included.Add(includedType);
                path.Push(includedType);
                Walk(includedType, (IArgonRole)Activator.CreateInstance(includedType)!, path);
                path.Pop();
            }
        }

        var stack = new Stack<Type>();
        stack.Push(roleType);
        Walk(roleType, root, stack);

        var featureGraph = FeatureGraph.Build(features.Declared, id);
        roleDiagnostics.AddRange(featureGraph.Diagnostics);

        foreach (var definition in featureGraph.Ordered)
        foreach (var contribute in definition.GrainRoots)
            contribute(grains);

        var declaredRoots = grains.CallRoots.ToHashSet();
        var scope         = Scope;
        var featureTypes  = featureGraph.Ordered.Select(f => f.FeatureType).ToArray();

        var lazyCallRoots = new Lazy<IReadOnlySet<Type>>(() =>
        {
            var registrations = new ServiceRegistrationScanner(scope);
            var all           = declaredRoots.ToHashSet();

            foreach (var featureType in featureTypes)
                all.UnionWith(registrations.RegistrationsOf(featureType));

            return all;
        });

        return new RoleDescriptor
        {
            Id                    = id,
            RoleType              = roleType,
            IsClient              = root.IsClient,
            ExposesClusterGateway = root.ExposesClusterGateway,
            UsesReminders         = root.UsesReminders,
            Description           = root.Description,
            HostedGrains          = grains.HostedGrains,
            LazyCallRoots         = lazyCallRoots,
            DynamicRefs           = grains.DynamicRefs,
            StartupCalls          = grains.StartupCalls,
            AcceptedRemotes       = grains.AcceptedRemotes,
            AllowedUnresolved     = grains.AllowedUnresolved,
            IncludedRoles         = included,
            Features              = featureGraph,
            Diagnostics           = roleDiagnostics
        };
    }

    private void DiscoverTopologies()
    {
        foreach (var type in ConcreteImplementationsOf(typeof(IArgonTopology)))
        {
            var nameProperty  = type.GetProperty(nameof(IArgonTopology.Name),
                BindingFlags.Public | BindingFlags.Static | BindingFlags.FlattenHierarchy);
            var rolesProperty = type.GetProperty(nameof(IArgonTopology.Roles),
                BindingFlags.Public | BindingFlags.Static | BindingFlags.FlattenHierarchy);

            if (nameProperty?.GetValue(null) is not string name || string.IsNullOrWhiteSpace(name) ||
                rolesProperty?.GetValue(null) is not ArgonRoleId[] topologyRoles)
            {
                diagnostics.Add(ClusterDiagnostic.Error("E9",
                    $"topology '{type.FullName}' must expose public static string Name and " +
                    $"public static ArgonRoleId[] Roles", target: type.FullName));
                continue;
            }

            if (topologies.TryGetValue(name, out var existing))
            {
                diagnostics.Add(ClusterDiagnostic.Error("E9",
                    $"duplicate topology '{name}' declared by '{existing.TopologyType.FullName}' and '{type.FullName}'",
                    target: type.FullName));
                continue;
            }

            topologies[name] = new TopologyDescriptor
            {
                Name         = name,
                TopologyType = type,
                Roles        = topologyRoles,
                Description  = ((IArgonTopology)Activator.CreateInstance(type)!).Description
            };
        }
    }

    private IEnumerable<Type> ConcreteImplementationsOf(Type contract)
        => Scope.Types()
          .Where(t => t is { IsClass: true, IsAbstract: false, ContainsGenericParameters: false })
          .Where(contract.IsAssignableFrom)
          .Where(t => t.GetConstructor(Type.EmptyTypes) is not null)
          .OrderBy(t => t.FullName, StringComparer.Ordinal);
}

public sealed class TopologyDescriptor
{
    public required string        Name         { get; init; }
    public required Type          TopologyType { get; init; }
    public required ArgonRoleId[] Roles        { get; init; }
    public          string        Description  { get; init; } = string.Empty;

    public override string ToString()
        => $"{Name} [{string.Join(", ", Roles.Select(r => r.Value))}]";
}
