namespace Argon.Features.Clustering;

/// <summary>
/// What discovery and analysis are allowed to look at: the assemblies to scan and, optionally, a
/// narrowing filter over the types within them.
/// </summary>
/// <remarks>
/// Kept as one object rather than two parameters so the catalog, the grain index and the scanner
/// cannot drift out of sync — a filter applied to one but not the others produces a graph that
/// validates against the wrong universe.
/// </remarks>
public sealed class ClusterScanScope
{
    public required IReadOnlyList<Assembly> Assemblies { get; init; }

    /// <summary>
    /// Narrows discovery to a subset of the scanned assemblies' types. <c>null</c> means everything.
    /// </summary>
    public Func<Type, bool>? TypeFilter { get; init; }

    public bool Includes(Type type)
        => TypeFilter?.Invoke(type) ?? true;

    /// <summary>
    /// Everything whose simple name starts with "Argon": what is loaded, plus what those assemblies
    /// reference. The product is split across assemblies now, and one of them being loaded later than
    /// the scan is not a reason for its grains to be invisible.
    /// </summary>
    public static ClusterScanScope Default()
    {
        var found = new Dictionary<string, Assembly>(StringComparer.Ordinal);
        var queue = new Queue<Assembly>(AppDomain.CurrentDomain.GetAssemblies().Where(IsProduct));

        if (Assembly.GetEntryAssembly() is { } entry && IsProduct(entry))
            queue.Enqueue(entry);

        while (queue.TryDequeue(out var assembly))
        {
            if (!found.TryAdd(assembly.FullName ?? assembly.GetName().Name!, assembly))
                continue;

            foreach (var reference in assembly.GetReferencedAssemblies())
            {
                if (reference.Name?.StartsWith("Argon", StringComparison.Ordinal) is not true)
                    continue;

                try
                {
                    queue.Enqueue(Assembly.Load(reference));
                }
                catch (Exception e) when (e is FileNotFoundException or FileLoadException or BadImageFormatException)
                {
                    // A reference the host never ships (a design-time-only package) is not ours to scan.
                }
            }
        }

        return new() { Assemblies = found.Values.ToArray() };

        static bool IsProduct(Assembly a)
            => !a.IsDynamic && a.GetName().Name?.StartsWith("Argon", StringComparison.Ordinal) is true;
    }

    public static ClusterScanScope For(Assembly assembly, Func<Type, bool>? typeFilter = null)
        => new()
        {
            Assemblies = [assembly],
            TypeFilter = typeFilter
        };

    public IEnumerable<Type> Types()
        => Assemblies.SelectMany(SafeGetTypes).Where(Includes);

    internal static IEnumerable<Type> SafeGetTypes(Assembly assembly)
    {
        try
        {
            return assembly.GetTypes();
        }
        catch (ReflectionTypeLoadException e)
        {
            return e.Types.Where(t => t is not null)!;
        }
    }
}
