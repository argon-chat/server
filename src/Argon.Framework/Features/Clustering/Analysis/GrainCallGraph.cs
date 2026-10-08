namespace Argon.Features.Clustering;

/// <summary>
/// A call site the scanner could not resolve to a concrete grain interface. Either a non-generic
/// <c>GetGrain(Type, …)</c> or a <c>GetGrain&lt;T&gt;</c> reached with <c>T</c> still an open
/// generic parameter. The owning role must waive it with <c>AllowUnresolved</c> or validation
/// fails with E7.
/// </summary>
public readonly record struct UnresolvedGrainCall(Type Root, Type DeclaringType, string MethodName, string Reason)
{
    public override string ToString()
        => $"{DeclaringType.FullName}.{MethodName} ({Reason})";
}

/// <summary>
/// The grain interfaces one root reaches, with the number of call sites behind each edge. The
/// weights are what makes W2 — "a heavy edge now crosses a role boundary" — checkable.
/// </summary>
public sealed class GrainCallSet
{
    public static readonly GrainCallSet Empty = new(new Dictionary<Type, int>());

    public GrainCallSet(IReadOnlyDictionary<Type, int> weights)
        => Weights = weights;

    public IReadOnlyDictionary<Type, int> Weights { get; }

    public IEnumerable<Type> Interfaces
        => Weights.Keys;

    public int WeightOf(Type grainInterface)
        => Weights.GetValueOrDefault(grainInterface);
}

/// <summary>
/// The result of analysing a set of roots: which grain interfaces each root reaches, how heavily,
/// and where the analysis gave up.
/// </summary>
public sealed class GrainCallGraph
{
    public required IReadOnlyDictionary<Type, GrainCallSet> ByRoot     { get; init; }
    public required IReadOnlyList<UnresolvedGrainCall>      Unresolved { get; init; }

    /// <summary>Union of every root's reachable grain interfaces.</summary>
    public IReadOnlySet<Type> All
        => allInterfaces ??= ByRoot.Values.SelectMany(x => x.Interfaces).ToHashSet();

    private HashSet<Type>? allInterfaces;

    public GrainCallSet From(Type root)
        => ByRoot.GetValueOrDefault(root) ?? GrainCallSet.Empty;

    public static GrainCallGraph Empty { get; } = new()
    {
        ByRoot     = new Dictionary<Type, GrainCallSet>(),
        Unresolved = []
    };
}

/// <summary>
/// Produces the grain call graph. Sits behind an interface so a compile-time source generator can
/// replace the runtime IL scan later without touching consumers.
/// </summary>
public interface IGrainGraphSource
{
    GrainCallGraph Analyze(IReadOnlyCollection<Type> roots);
}
