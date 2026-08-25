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

    /// <summary>Everything loaded whose simple name starts with "Argon".</summary>
    public static ClusterScanScope Default()
        => new()
        {
            Assemblies = AppDomain.CurrentDomain
               .GetAssemblies()
               .Where(a => !a.IsDynamic && a.GetName().Name?.StartsWith("Argon", StringComparison.Ordinal) is true)
               .DistinctBy(a => a.FullName)
               .ToArray()
        };

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
