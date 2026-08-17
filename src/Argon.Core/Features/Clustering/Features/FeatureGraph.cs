namespace Argon.Features.Clustering;

/// <summary>
/// The resolved feature set of a single role: declared features plus their transitive
/// <see cref="IFeatureDescriptor.Requires{T}"/> closure, topologically sorted.
/// </summary>
/// <remarks>
/// This is the one place in the design where a transitive closure is actually computed. It is
/// bounded by the feature count rather than by the grain graph, which is why it terminates
/// usefully where a naive grain closure would not.
/// </remarks>
public sealed class FeatureGraph
{
    private FeatureGraph(IReadOnlyList<FeatureDefinition> ordered, IReadOnlyList<ClusterDiagnostic> diagnostics)
    {
        Ordered     = ordered;
        Diagnostics = diagnostics;
    }

    /// <summary>Features in the order <c>Configure</c> and <c>Map</c> must run.</summary>
    public IReadOnlyList<FeatureDefinition> Ordered { get; }

    public IReadOnlyList<ClusterDiagnostic> Diagnostics { get; }

    public bool HasErrors
        => Diagnostics.Any(d => d.Severity is ClusterDiagnosticSeverity.Error);

    public static FeatureGraph Build(IReadOnlyList<Type> declared, ArgonRoleId role)
    {
        var diagnostics = new List<ClusterDiagnostic>();
        var defs        = new Dictionary<Type, FeatureDefinition>();

        // 1. Transitive closure over Requires.
        var pending = new Queue<Type>(declared);
        while (pending.Count > 0)
        {
            var type = pending.Dequeue();
            if (defs.ContainsKey(type))
                continue;

            FeatureDefinition def;
            try
            {
                def = FeatureCatalog.Describe(type);
            }
            catch (Exception e)
            {
                diagnostics.Add(ClusterDiagnostic.Error("E8",
                    $"feature '{type.FullName}' could not be described: {e.Message}", role, type.FullName));
                continue;
            }

            defs[type] = def;
            foreach (var required in def.Requires)
                if (!defs.ContainsKey(required))
                    pending.Enqueue(required);
        }

        // 2. Conflicts.
        foreach (var def in defs.Values)
        foreach (var conflict in def.Conflicts)
            if (defs.TryGetValue(conflict, out var other))
                diagnostics.Add(ClusterDiagnostic.Error("E8",
                    $"features '{def.Name}' and '{other.Name}' conflict but are both enabled", role, def.Name));

        // 3. Edges. Requires and After point at us; Before points away from us.
        var edges    = new Dictionary<Type, HashSet<Type>>();
        var inDegree = defs.Keys.ToDictionary(t => t, _ => 0);

        void Edge(Type from, Type to)
        {
            if (!defs.ContainsKey(from) || !defs.ContainsKey(to) || from == to)
                return;
            if (!edges.TryGetValue(from, out var set))
                edges[from] = set = [];
            if (set.Add(to))
                inDegree[to]++;
        }

        foreach (var def in defs.Values)
        {
            foreach (var dependency in def.Requires)
                Edge(dependency, def.FeatureType);
            foreach (var earlier in def.After)
                Edge(earlier, def.FeatureType);
            foreach (var later in def.Before)
                Edge(def.FeatureType, later);
        }

        // 4. Kahn, name-ordered so the result is stable across runs.
        var ready = new List<Type>(defs.Keys.Where(t => inDegree[t] == 0));
        var order = new List<FeatureDefinition>(defs.Count);

        while (ready.Count > 0)
        {
            ready.Sort(static (a, b) => string.CompareOrdinal(
                FeatureCatalog.Describe(a).Name, FeatureCatalog.Describe(b).Name));

            var next = ready[0];
            ready.RemoveAt(0);
            order.Add(defs[next]);

            if (!edges.TryGetValue(next, out var targets))
                continue;
            foreach (var target in targets)
                if (--inDegree[target] == 0)
                    ready.Add(target);
        }

        if (order.Count != defs.Count)
        {
            var cyclic = defs.Keys.Where(t => inDegree[t] > 0).Select(t => defs[t].Name).Order().ToArray();
            diagnostics.Add(ClusterDiagnostic.Error("E8",
                $"feature dependency cycle among: {string.Join(", ", cyclic)}", role));
        }

        return new FeatureGraph(order, diagnostics);
    }
}
