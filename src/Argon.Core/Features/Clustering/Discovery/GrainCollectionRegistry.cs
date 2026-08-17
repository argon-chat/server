namespace Argon.Features.Clustering;

/// <summary>
/// Accumulates one role's declarations. Composition via <see cref="Include{TRole}"/> is recorded
/// here and resolved by <see cref="ArgonClusterCatalog"/>, which owns the include graph and its
/// cycle detection.
/// </summary>
internal sealed class GrainCollectionRegistry : IGrainCollectionRegistry
{
    public HashSet<Type>            HostedGrains     { get; } = [];
    public HashSet<Type>            CallRoots        { get; } = [];
    public HashSet<Type>            DynamicRefs      { get; } = [];
    public HashSet<Type>            StartupCalls     { get; } = [];
    public List<Type>               Includes         { get; } = [];
    public Dictionary<Type, string> AcceptedRemotes  { get; } = [];
    public Dictionary<Type, string> AllowedUnresolved { get; } = [];

    public void AddToRef<T>() where T : class, IGrain
    {
        var type = typeof(T);
        if (type.IsAbstract || type.IsInterface)
            throw new ArgumentException(
                $"AddToRef expects a concrete grain class, got '{type.FullName}'. " +
                $"Grain interfaces are call edges and are derived by analysis, not declared.");
        HostedGrains.Add(type);
    }

    public void AddCallRoot<T>()
        => CallRoots.Add(typeof(T));

    public void AddCallRoot(Type root)
        => CallRoots.Add(root);

    public void Include<TRole>() where TRole : IArgonRole, new()
        => Includes.Add(typeof(TRole));

    public void AddStartupCall<TInterface>() where TInterface : IGrain
        => StartupCalls.Add(typeof(TInterface));

    public void AddDynamicRef<TInterface>() where TInterface : IGrain
        => DynamicRefs.Add(typeof(TInterface));

    public void AllowUnresolved<TDeclaringType>(string reason)
    {
        if (string.IsNullOrWhiteSpace(reason))
            throw new ArgumentException(
                $"AllowUnresolved<{typeof(TDeclaringType).Name}> requires a reason — it records why the " +
                $"dynamic dispatch there is safe.", nameof(reason));
        AllowedUnresolved[typeof(TDeclaringType)] = reason;
    }

    public void AcceptRemote<TInterface>(string reason) where TInterface : IGrain
    {
        if (string.IsNullOrWhiteSpace(reason))
            throw new ArgumentException(
                $"AcceptRemote<{typeof(TInterface).Name}> requires a reason — it records why the " +
                $"remote hop is preferred over co-hosting the worker.", nameof(reason));
        AcceptedRemotes[typeof(TInterface)] = reason;
    }

    public void MergeInto(GrainCollectionRegistry target)
    {
        target.HostedGrains.UnionWith(HostedGrains);
        target.CallRoots.UnionWith(CallRoots);
        target.DynamicRefs.UnionWith(DynamicRefs);
        target.StartupCalls.UnionWith(StartupCalls);
        foreach (var (type, reason) in AcceptedRemotes)
            target.AcceptedRemotes.TryAdd(type, reason);
        foreach (var (type, reason) in AllowedUnresolved)
            target.AllowedUnresolved.TryAdd(type, reason);
    }
}
