namespace Argon.Features.Clustering;

/// <summary>
/// What a feature registers, read out of its own <c>Configure</c> and <c>Map</c> bodies.
/// </summary>
/// <remarks>
/// A feature already says what it brings — <c>AddSingleton&lt;IFoo, Foo&gt;()</c>,
/// <c>AddService&lt;IUserInteraction, UserInteractionImpl&gt;()</c>, <c>MapHub&lt;AppHub&gt;()</c>.
/// Making it repeat the same list as <c>GrainRoots(g =&gt; g.AddCallRoot&lt;…&gt;())</c> is the
/// boilerplate: two declarations of one fact, which drift.
/// <para>
/// The walk descends through the product's own extension methods, so a feature whose whole body is
/// <c>ctx.Builder.AddArgonAuthorization()</c> still yields what that method registers.
/// </para>
/// </remarks>
public sealed class ServiceRegistrationScanner(ClusterScanScope scope)
{
    /// <summary>Registration methods whose type arguments name an implementation we should walk.</summary>
    private static readonly HashSet<string> RegistrationMethods = new(StringComparer.Ordinal)
    {
        "AddSingleton", "AddScoped", "AddTransient",
        "TryAddSingleton", "TryAddScoped", "TryAddTransient",
        "AddKeyedSingleton", "AddKeyedScoped", "AddKeyedTransient",
        "AddHostedService",
        "AddService",       // Ion service registration
        "MapHub",           // SignalR
        "AddScheme"         // authentication handlers
    };

    /// <summary>
    /// Calls that register a whole family of types by convention rather than by naming them.
    /// <c>AddControllers()</c> hands MVC every <c>ControllerBase</c> it can find; <c>MapBotApi()</c>
    /// does the same for the bot interfaces. Nothing in the IL names those types, so the scanner
    /// mirrors the convention the framework applies at run time.
    /// </summary>
    private static readonly (string Method, string MarkerType)[] ConventionRoots =
    [
        ("AddControllers", "Microsoft.AspNetCore.Mvc.ControllerBase"),
        ("MapBotApi",      "Argon.Features.BotApi.IBotInterface")
    ];

    private readonly HashSet<Assembly>                        scanned = scope.Assemblies.ToHashSet();
    private readonly ConcurrentDictionary<MethodBase, Type[]> cache   = new();

    private readonly Lazy<Type[]> concreteTypes = new(() => scope.Types()
       .Where(t => t is { IsClass: true, IsAbstract: false, ContainsGenericParameters: false })
       .ToArray());

    private readonly ConcurrentDictionary<string, Type[]> conventionCache = new(StringComparer.Ordinal);

    /// <summary>
    /// Implementation types the feature registers, transitively through its own extension methods.
    /// </summary>
    public IReadOnlySet<Type> RegistrationsOf(Type featureType)
    {
        var found   = new HashSet<Type>();
        var visited = new HashSet<MethodBase>();
        var queue   = new Queue<MethodBase>();

        foreach (var method in featureType.GetMethods(
                     BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Instance | BindingFlags.DeclaredOnly))
            if (visited.Add(method))
                queue.Enqueue(method);

        while (queue.Count > 0)
        {
            var method = queue.Dequeue();

            foreach (var registered in Registrations(method))
                found.Add(registered);

            foreach (var callee in Callees(method))
                if (visited.Add(callee))
                    queue.Enqueue(callee);
        }

        return found;
    }

    private Type[] Registrations(MethodBase method)
        => cache.GetOrAdd(method, m =>
        {
            var result = new List<Type>();

            foreach (var callee in ResolveCalls(m))
            {
                foreach (var (method, marker) in ConventionRoots)
                    if (callee.Name == method)
                        result.AddRange(ImplementorsOf(marker));

                if (callee is not MethodInfo { IsGenericMethod: true } generic ||
                    !RegistrationMethods.Contains(callee.Name))
                    continue;

                // The implementation is the last type argument: AddSingleton<TImpl>,
                // AddSingleton<TService, TImpl>, AddScheme<TOptions, THandler> all put it there.
                var arguments = generic.GetGenericArguments();
                var candidate = arguments[^1];

                if (candidate is { IsClass: true, IsAbstract: false, ContainsGenericParameters: false } &&
                    scanned.Contains(candidate.Assembly))
                    result.Add(candidate);
            }

            return result.ToArray();
        });

    /// <summary>Concrete scanned types assignable to a marker named by its full name.</summary>
    private Type[] ImplementorsOf(string markerTypeName)
        => conventionCache.GetOrAdd(markerTypeName, name =>
        {
            var marker = concreteTypes.Value
               .SelectMany(t => t.GetInterfaces().Cast<Type>().Append(t))
               .FirstOrDefault(t => t.FullName == name)
                      ?? concreteTypes.Value.Select(t => t.BaseType).FirstOrDefault(t => t?.FullName == name);

            return marker is null
                ? []
                : concreteTypes.Value.Where(t => t != marker && marker.IsAssignableFrom(t)).ToArray();
        });

    /// <summary>Calls into the product's own code, so the walk follows extension methods.</summary>
    private IEnumerable<MethodBase> Callees(MethodBase method)
    {
        foreach (var callee in ResolveCalls(method))
        {
            var owner = callee.DeclaringType;
            if (owner is null || !scanned.Contains(owner.Assembly) || owner.IsInterface)
                continue;
            yield return callee;
        }
    }

    private static IEnumerable<MethodBase> ResolveCalls(MethodBase method)
    {
        byte[]? il;
        try
        {
            il = method.GetMethodBody()?.GetILAsByteArray();
        }
        catch
        {
            yield break;
        }

        if (il is null || il.Length == 0)
            yield break;

        var typeArgs   = method.DeclaringType is { IsGenericType: true } t ? t.GetGenericArguments() : null;
        var methodArgs = method is MethodInfo { IsGenericMethod: true } mi ? mi.GetGenericArguments() : null;

        foreach (var token in IlCallWalker.CallTokens(il))
        {
            MethodBase? callee = null;
            try
            {
                callee = method.Module.ResolveMethod(token, typeArgs, methodArgs);
            }
            catch
            {
                // varargs, unloadable type, or a token from another context
            }

            if (callee is not null)
                yield return callee;
        }
    }
}
