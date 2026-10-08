namespace Argon.Features.Clustering;

using System.Runtime.CompilerServices;

/// <summary>
/// Derives the grain call graph by decoding method bodies at runtime.
/// </summary>
/// <remarks>
/// Traversal rule:
/// follow calls transitively inside the scanned assemblies, record an edge on
/// <c>GetGrain&lt;IFoo&gt;</c> and do <b>not</b> descend into the implementing grain class —
/// grain classes are roots on whichever role hosts them. This is what keeps the closure one hop
/// deep instead of swallowing the whole graph.
/// <para>
/// Decoding is cached per method and shared across roles; only the cheap breadth-first walk is
/// repeated per root.
/// </para>
/// </remarks>
public sealed class IlGrainGraphScanner : IGrainGraphSource
{
    private readonly HashSet<Assembly>                            scanned;
    private readonly GrainTypeIndex                               grains;
    private readonly ConcurrentDictionary<MethodBase, MethodScan> methodScans  = new();
    private readonly ConcurrentDictionary<Type, Type[]>           implementors = new();
    private readonly Lazy<Type[]>                                 concreteTypes;

    public IlGrainGraphScanner(ClusterScanScope scope, GrainTypeIndex grainIndex)
    {
        scanned       = scope.Assemblies.ToHashSet();
        grains        = grainIndex;
        concreteTypes = new Lazy<Type[]>(() => scope.Types()
           .Where(t => t is { IsClass: true, IsAbstract: false, ContainsGenericParameters: false })
           .ToArray());
    }

    public GrainCallGraph Analyze(IReadOnlyCollection<Type> roots)
    {
        var byRoot     = new Dictionary<Type, GrainCallSet>();
        var unresolved = new List<UnresolvedGrainCall>();

        foreach (var root in roots)
        {
            var reached     = new Dictionary<Type, int>();
            var visited     = new HashSet<MethodBase>();
            var queue       = new Queue<MethodBase>();
            var isGrainRoot = grains.Classes.ContainsKey(root);

            foreach (var method in EntryMethods(root))
                if (visited.Add(method))
                    queue.Enqueue(method);

            while (queue.Count > 0)
            {
                var scan = ScanMethod(queue.Dequeue());

                foreach (var (grainInterface, weight) in scan.GrainInterfaces)
                    reached[grainInterface] = reached.GetValueOrDefault(grainInterface) + weight;

                foreach (var site in scan.Unresolved)
                    unresolved.Add(new UnresolvedGrainCall(root, site.DeclaringType, site.MethodName, site.Reason));

                foreach (var callee in scan.Callees)
                {
                    // Another role's grain is a cluster boundary, not a continuation of this walk.
                    // The primary boundary is the interface dispatch rule in DispatchTargets, which
                    // never expands a grain interface into its implementation. This is the backstop
                    // for the ways a grain class can be entered without going through one: a static
                    // helper on the class, or a non-grain interface it happens to implement.
                    var owner = callee.DeclaringType;
                    if (owner is not null && grains.Classes.ContainsKey(owner) && !(isGrainRoot && owner == root))
                        continue;
                    if (visited.Add(callee))
                        queue.Enqueue(callee);
                }
            }

            byRoot[root] = new GrainCallSet(reached);
        }

        return new GrainCallGraph
        {
            ByRoot     = byRoot,
            Unresolved = unresolved
        };
    }

    /// <summary>
    /// A root contributes its own methods plus those of its nested types — async state machines and
    /// closure classes live there, and that is where the real call sites are.
    /// </summary>
    private static IEnumerable<MethodBase> EntryMethods(Type root)
    {
        const BindingFlags flags = BindingFlags.Public | BindingFlags.NonPublic
                                 | BindingFlags.Instance | BindingFlags.Static | BindingFlags.DeclaredOnly;

        var stack = new Stack<Type>();
        stack.Push(root);

        while (stack.Count > 0)
        {
            var type = stack.Pop();

            foreach (var nested in type.GetNestedTypes(BindingFlags.Public | BindingFlags.NonPublic))
                if (!nested.ContainsGenericParameters)
                    stack.Push(nested);

            if (type.ContainsGenericParameters)
                continue;

            foreach (var method in type.GetMethods(flags))
                yield return method;
            foreach (var ctor in type.GetConstructors(flags))
                yield return ctor;
        }
    }

    private MethodScan ScanMethod(MethodBase method)
        => methodScans.GetOrAdd(method, Decode);

    private MethodScan Decode(MethodBase method)
    {
        var result = new MethodScan();

        // async/iterator bodies are stubs; the real code is in the generated state machine.
        var stateMachine = method.GetCustomAttribute<AsyncStateMachineAttribute>()?.StateMachineType
                        ?? method.GetCustomAttribute<IteratorStateMachineAttribute>()?.StateMachineType;
        if (stateMachine is not null && !stateMachine.ContainsGenericParameters)
            result.Callees.AddRange(stateMachine.GetMethods(
                BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Instance | BindingFlags.DeclaredOnly));

        byte[]? il;
        try
        {
            il = method.GetMethodBody()?.GetILAsByteArray();
        }
        catch
        {
            return result;   // abstract, extern, or a body the runtime will not hand over.
        }

        if (il is null || il.Length == 0)
            return result;

        var typeArgs   = SafeGenericArguments(method.DeclaringType);
        var methodArgs = method is MethodInfo { IsGenericMethod: true } mi ? SafeGenericArguments(mi) : null;
        var module     = method.Module;

        foreach (var token in IlCallWalker.CallTokens(il))
        {
            MethodBase? callee;
            try
            {
                callee = module.ResolveMethod(token, typeArgs, methodArgs);
            }
            catch
            {
                continue;    // varargs, unloadable type, or a token from another context.
            }

            if (callee is null)
                continue;

            if (TryRecordGrainCall(method, callee, result))
                continue;    // Orleans' factory, never our code — nothing to descend into.

            foreach (var target in DispatchTargets(callee))
                result.Callees.Add(target);
        }

        return result;
    }

    /// <summary>
    /// Recognises a grain factory call and records what it resolves to.
    /// </summary>
    /// <returns>
    /// <c>true</c> when the call site was a grain factory call — resolved or not. <c>false</c> lets
    /// an unrelated method that merely happens to be named <c>GetGrain</c> fall through to the
    /// normal walk instead of being reported as unresolvable.
    /// </returns>
    private static bool TryRecordGrainCall(MethodBase caller, MethodBase callee, MethodScan result)
    {
        if (callee.Name is not "GetGrain" || callee is not MethodInfo method)
            return false;

        var owner = caller.DeclaringType ?? typeof(void);

        if (!method.IsGenericMethod)
        {
            // GetGrain(Type, …) and GetGrain(GrainId, …) name the grain at run time only.
            // Anything else called "GetGrain" is not the factory and falls through to the walk.
            if (!IsGrainFactoryCall(method))
                return false;

            result.Unresolved.Add(new UnresolvedSite(owner, caller.Name,
                $"non-generic {method.Name}({string.Join(", ", method.GetParameters().Select(p => p.ParameterType.Name))}) " +
                $"cannot be resolved statically"));
            return true;
        }

        var arguments = method.GetGenericArguments();

        if (arguments.FirstOrDefault(a => a.IsGenericParameter) is { } open)
        {
            result.Unresolved.Add(new UnresolvedSite(owner, caller.Name,
                $"GetGrain<{open.Name}> reached with an open generic parameter"));
            return true;
        }

        var recorded = false;
        foreach (var argument in arguments.Where(GrainTypeIndex.IsGrainInterface))
        {
            result.GrainInterfaces[argument] = result.GrainInterfaces.GetValueOrDefault(argument) + 1;
            recorded = true;
        }

        return recorded;
    }

    /// <summary>
    /// Whether a non-generic <c>GetGrain</c> is Orleans' factory rather than an unrelated method of
    /// the same name. Covers both the interface itself and extension methods that take the factory
    /// or name the grain by <see cref="Type"/>.
    /// </summary>
    private static bool IsGrainFactoryCall(MethodInfo method)
        => typeof(IGrainFactory).IsAssignableFrom(method.DeclaringType)
        || method.GetParameters().Any(p => p.ParameterType == typeof(Type)
                                        || typeof(IGrainFactory).IsAssignableFrom(p.ParameterType));

    /// <summary>
    /// Expands a call site into the bodies that can actually run. An interface or virtual call
    /// resolves to the declaration, which has no body — without this every service reached through
    /// its interface would silently terminate the walk.
    /// </summary>
    private IEnumerable<MethodBase> DispatchTargets(MethodBase callee)
    {
        var owner = callee.DeclaringType;
        if (owner is null || !scanned.Contains(owner.Assembly))
            yield break;

        if (!owner.IsInterface)
        {
            yield return callee;

            if (callee is not MethodInfo { IsVirtual: true, IsFinal: false } virtualMethod)
                yield break;

            var baseDefinition = virtualMethod.GetBaseDefinition();
            foreach (var implementor in ImplementorsOf(owner))
            foreach (var candidate in implementor.GetMethods(
                         BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Instance | BindingFlags.DeclaredOnly))
                if (candidate.GetBaseDefinition() == baseDefinition)
                    yield return candidate;

            yield break;
        }

        if (GrainTypeIndex.IsGrainInterface(owner))
            yield break;    // a grain call: the boundary, handled by the hosting role.

        foreach (var implementor in ImplementorsOf(owner))
        {
            InterfaceMapping map;
            try
            {
                map = implementor.GetInterfaceMap(owner);
            }
            catch
            {
                continue;   // constructed generics and some shapes refuse mapping.
            }

            for (var i = 0; i < map.InterfaceMethods.Length; i++)
                if (map.InterfaceMethods[i] == callee)
                    yield return map.TargetMethods[i];
        }
    }

    /// <summary>
    /// Concrete scanned types assignable to <paramref name="contract"/>. Bounded in practice by
    /// <see cref="DispatchTargets"/> rejecting anything outside the scanned assemblies before it
    /// gets here, so framework interfaces never reach this path.
    /// </summary>
    private Type[] ImplementorsOf(Type contract)
        => implementors.GetOrAdd(contract, c => concreteTypes.Value.Where(t => t != c && c.IsAssignableFrom(t)).ToArray());

    private static Type[]? SafeGenericArguments(MemberInfo? member)
    {
        try
        {
            return member switch
            {
                Type { IsGenericType: true } type       => type.GetGenericArguments(),
                MethodInfo { IsGenericMethod: true } mi => mi.GetGenericArguments(),
                _                                      => null
            };
        }
        catch
        {
            return null;
        }
    }

    private readonly record struct UnresolvedSite(Type DeclaringType, string MethodName, string Reason);

    private sealed class MethodScan
    {
        /// <summary>Grain interface to the number of call sites in this method reaching it.</summary>
        public Dictionary<Type, int> GrainInterfaces { get; } = [];

        public List<MethodBase>     Callees    { get; } = [];
        public List<UnresolvedSite> Unresolved { get; } = [];
    }
}
