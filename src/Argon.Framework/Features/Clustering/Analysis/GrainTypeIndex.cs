namespace Argon.Features.Clustering;

using Orleans.Concurrency;

/// <summary>
/// Everything validation needs to know about the grain types in a set of assemblies: which classes
/// are grains, which interfaces they implement, and the placement/persistence metadata that decides
/// where a grain is allowed to live.
/// </summary>
public sealed class GrainTypeIndex
{
    private readonly Dictionary<Type, GrainClassInfo> classes         = [];
    private readonly Dictionary<Type, List<Type>>     byInterface     = [];
    private readonly HashSet<Type>                    interfaces      = [];
    private readonly HashSet<Assembly>                ownedAssemblies;

    private GrainTypeIndex(HashSet<Assembly> owned)
        => ownedAssemblies = owned;

    public IReadOnlyDictionary<Type, GrainClassInfo> Classes => classes;

    /// <summary>Grain interfaces declared in the scanned assemblies.</summary>
    public IReadOnlySet<Type> Interfaces => interfaces;

    public static GrainTypeIndex Build(ClusterScanScope scope)
    {
        var owned = scope.Assemblies.ToHashSet();
        var index = new GrainTypeIndex(owned);

        foreach (var type in scope.Types())
        {
            if (type.ContainsGenericParameters)
                continue;

            if (type.IsInterface)
            {
                if (IsGrainInterface(type))
                    index.interfaces.Add(type);
                continue;
            }

            if (!type.IsClass || type.IsAbstract || !typeof(IGrain).IsAssignableFrom(type))
                continue;

            // Orleans' source generator emits a Proxy_IFoo : GrainReference for every grain
            // interface. Those implement IGrain but are the caller's side of the wire, not
            // something a silo hosts — counting them would make every proxy an orphan grain (E2).
            if (typeof(GrainReference).IsAssignableFrom(type))
                continue;

            var implemented = type.GetInterfaces()
               .Where(IsGrainInterface)
               .Where(i => owned.Contains(i.Assembly))
               .ToArray();

            index.classes[type] = new GrainClassInfo
            {
                ClassType         = type,
                Interfaces        = implemented,
                IsStatelessWorker = type.GetCustomAttribute<StatelessWorkerAttribute>(inherit: true) is not null,
                IsRemindable      = typeof(IRemindable).IsAssignableFrom(type),
                StorageProviders  = ReadStorageProviders(type)
            };

            foreach (var contract in implemented)
            {
                if (!index.byInterface.TryGetValue(contract, out var list))
                    index.byInterface[contract] = list = [];
                list.Add(type);
            }
        }

        return index;
    }

    public GrainClassInfo? Info(Type grainClass)
        => classes.GetValueOrDefault(grainClass);

    /// <summary>
    /// Whether a grain interface belongs to the scanned assemblies, and is therefore ours to host.
    /// </summary>
    /// <remarks>
    /// Orleans ships grain interfaces of its own — <c>IManagementGrain</c> is the one the admin
    /// console uses — implemented inside the runtime and available on every silo. Requiring a role
    /// to host them would fail validation for something no role can host.
    /// </remarks>
    public bool IsOwned(Type grainInterface)
        => ownedAssemblies.Contains(grainInterface.Assembly);

    /// <summary>Grain classes implementing <paramref name="grainInterface"/>, empty when none exist.</summary>
    public IReadOnlyList<Type> Implementations(Type grainInterface)
        => byInterface.GetValueOrDefault(grainInterface) ?? (IReadOnlyList<Type>)[];

    /// <summary>
    /// The grain interfaces a set of hosted classes makes available. Used to turn a role's hosted
    /// set into the set of call edges it satisfies.
    /// </summary>
    public HashSet<Type> InterfacesOf(IEnumerable<Type> grainClasses)
    {
        var result = new HashSet<Type>();
        foreach (var grainClass in grainClasses)
            if (classes.TryGetValue(grainClass, out var info))
                result.UnionWith(info.Interfaces);
        return result;
    }

    /// <summary>
    /// A grain interface is one that derives from <see cref="IGrain"/> without being one of Orleans'
    /// own key-shape marker interfaces.
    /// </summary>
    public static bool IsGrainInterface(Type type)
        => type is { IsInterface: true }
        && typeof(IGrain).IsAssignableFrom(type)
        && type != typeof(IGrain)
        && type != typeof(IGrainWithGuidKey)
        && type != typeof(IGrainWithStringKey)
        && type != typeof(IGrainWithIntegerKey)
        && type != typeof(IGrainWithGuidCompoundKey)
        && type != typeof(IGrainWithIntegerCompoundKey)
        && type != typeof(IGrainObserver)
        && type != typeof(IAddressable);

    private static IReadOnlyList<GrainStorageBinding> ReadStorageProviders(Type grainClass)
    {
        List<GrainStorageBinding>? bindings = null;

        foreach (var ctor in grainClass.GetConstructors())
        foreach (var parameter in ctor.GetParameters())
        {
            if (parameter.GetCustomAttribute<PersistentStateAttribute>() is not { } state)
                continue;
            bindings ??= [];
            bindings.Add(new GrainStorageBinding(state.StateName, state.StorageName));
        }

        return bindings ?? (IReadOnlyList<GrainStorageBinding>)[];
    }
}

public sealed class GrainClassInfo
{
    public required Type                ClassType         { get; init; }
    public required IReadOnlyList<Type> Interfaces        { get; init; }
    public required bool                IsStatelessWorker { get; init; }
    public required bool                IsRemindable      { get; init; }

    public required IReadOnlyList<GrainStorageBinding> StorageProviders { get; init; }

    public override string ToString()
        => ClassType.Name;
}

/// <summary>One <c>[PersistentState(stateName, storageName)]</c> declaration on a grain constructor.</summary>
public readonly record struct GrainStorageBinding(string? StateName, string? StorageName);
