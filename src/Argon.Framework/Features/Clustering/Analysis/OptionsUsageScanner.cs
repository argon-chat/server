namespace Argon.Features.Clustering;

/// <summary>
/// What a feature <i>reads</i>, as opposed to what it declared it would read.
/// </summary>
/// <remarks>
/// <para><b>Written after a production outage nothing could see.</b> A feature mapped an endpoint
/// whose handler takes <c>IOptions&lt;StorageOptions&gt;</c> and declared only its own options
/// section. On a role that also ran the storage feature the section was bound by that one, and
/// everything worked. On the role that actually serves the endpoint in production it was not, so the
/// handler ran against a default-constructed instance: no regional origins, so the redirect it sent
/// was a relative path the caller resolved against the API and got a 404, and no cache window, so
/// every image fetch paid for it again. The process started, stayed healthy, logged nothing, and
/// every avatar in the product was broken.</para>
///
/// <para>The co-hosted integration host cannot see this: it composes every role into one process, so
/// some feature always declares the section. Only a per-role check finds it, and only by comparing
/// what the code reaches for against what the role declared.</para>
///
/// <para>The walk is the one <see cref="ServiceRegistrationScanner"/> uses — through the product's
/// own extension methods, and through <c>ldftn</c>, which is how a minimal-API handler is reached.
/// The handler's <i>parameters</i> are the interesting part: nothing in the mapping call names
/// <c>StorageOptions</c>, the handler's signature does.</para>
/// </remarks>
public sealed class OptionsUsageScanner(ClusterScanScope scope)
{
    /// <summary>The wrappers a settings class is asked for through.</summary>
    private static readonly HashSet<string> Accessors = new(StringComparer.Ordinal)
    {
        "IOptions`1", "IOptionsSnapshot`1", "IOptionsMonitor`1"
    };

    private readonly HashSet<Assembly>                        scanned = scope.Assemblies.ToHashSet();
    private readonly ConcurrentDictionary<MethodBase, Type[]> cache   = new();

    private readonly Lazy<Type[]> concreteTypes = new(() => scope.Types()
       .Where(t => t is { IsClass: true, IsAbstract: false, ContainsGenericParameters: false })
       .ToArray());

    private readonly ConcurrentDictionary<string, Type[]> conventionCache = new(StringComparer.Ordinal);

    /// <summary>
    /// Settings types the feature's own code reaches for, transitively.
    /// </summary>
    /// <summary>
    /// How far the walk follows a feature's own calls.
    /// </summary>
    /// <remarks>
    /// <para>Bounded, and it has to be. Everything in the product is one assembly graph, so an
    /// unbounded walk from any feature eventually reaches most of it and reports settings the feature
    /// has nothing to do with — which is worse than reporting none, because a check that cries wolf
    /// gets its findings waived.</para>
    ///
    /// <para>Four hops is what the shape being caught actually needs: a feature's <c>Map</c>, the
    /// extension method it calls, the handler that method registers, and one indirection inside it.
    /// The redirect this was written for is three.</para>
    /// </remarks>
    private const int MaxDepth = 4;

    /// <summary>
    /// Settings types a class asks for in its constructors — what a hosted grain needs bound on the
    /// role that activates it.
    /// </summary>
    /// <remarks>
    /// <para>A grain is not reached by the walk from a feature: Orleans activates it from the role's
    /// grain registry, and nothing in any feature's code names its constructor. So a grain that takes
    /// <c>IOptions&lt;T&gt;</c> on a role whose features never declared <c>T</c> got a default
    /// instance and ran on it — which is how the trust grain sat on <c>core</c> reading an
    /// <c>IsEnabled</c> that was always false, and every trust score in production was the default of
    /// an empty section.</para>
    ///
    /// <para>Constructors only, no walk: what a grain's methods reach for through the container is
    /// the grain's business and rare; what it cannot be activated without is in its signature.</para>
    /// </remarks>
    public IReadOnlySet<Type> ConstructedWith(Type type)
    {
        var found = new HashSet<Type>();
        var into  = new List<Type>();

        foreach (var constructor in type.GetConstructors(BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Instance))
        foreach (var parameter in constructor.GetParameters())
            Collect(parameter.ParameterType, into);

        found.UnionWith(into);

        return found;
    }

    public IReadOnlySet<Type> UsagesOf(Type featureType)
        => UsagesOf(featureType, null);

    /// <summary>
    /// What a feature reads, counting a reflection-adopted family only where it can be built.
    /// </summary>
    /// <param name="activatable">
    /// Whether the role under consideration can construct a given type. Null counts every adopted
    /// type, which is what a caller asking about a feature in isolation wants.
    /// </param>
    /// <remarks>
    /// <para>Without this, adopting a family over-reports. Every role mapping controllers is handed
    /// every <c>ControllerBase</c> in the product — including the identity server's, whose
    /// constructors need services only the Aegis features register. MVC does route them there, so
    /// the type really is adopted; what it cannot do is <i>run</i>, because activation fails on the
    /// first missing dependency. Reporting the settings it would have read is a finding about code
    /// that cannot execute, and a check that reports those gets waived along with the true ones.</para>
    ///
    /// <para>The bot interfaces are the other side of the same test: theirs need a grain factory and
    /// their own settings, both present wherever the bot API is mapped, so they are counted — which
    /// is what catches a role serving them without the section they read.</para>
    /// </remarks>
    public IReadOnlySet<Type> UsagesOf(Type featureType, Func<Type, bool>? activatable)
    {
        var found   = new HashSet<Type>();
        var visited = new HashSet<MethodBase>();
        var queue   = new Queue<(MethodBase Method, int Depth)>();

        foreach (var method in featureType.GetMethods(
                     BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Instance | BindingFlags.DeclaredOnly))
            if (visited.Add(method))
                queue.Enqueue((method, 0));

        while (queue.Count > 0)
        {
            var (method, depth) = queue.Dequeue();

            foreach (var used in Usages(method))
                found.Add(used);

            // A call that hands a framework a whole family of types brings that family's own
            // constructors into this feature's reach, even though nothing in the IL names them.
            foreach (var adopted in AdoptedByConvention(method))
            {
                if (activatable is not null && !activatable(adopted))
                    continue;

                found.UnionWith(ConstructedWith(adopted));

                foreach (var own in adopted.GetMethods(
                             BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Instance | BindingFlags.DeclaredOnly))
                    if (visited.Add(own))
                        queue.Enqueue((own, depth));
            }

            if (depth >= MaxDepth)
                continue;

            foreach (var callee in Callees(method))
                if (visited.Add(callee))
                    queue.Enqueue((callee, depth + 1));
        }

        return found;
    }

    /// <summary>
    /// Types this method hands to a framework wholesale, by calling something that finds them itself.
    /// </summary>
    /// <remarks>
    /// <para><b>The gap this closes cost an outage.</b> The bot API's <c>MapBotApi()</c> scans loaded
    /// assemblies for bot interfaces and builds each through <c>ActivatorUtilities</c>, so their
    /// constructor parameters are resolved by reflection at run time. One of them takes
    /// <c>IOptions&lt;CallKitOptions&gt;</c> to tell an accepting bot where the audio ingress is —
    /// and the role serving the bot API declared no such section, so the binder produced a default
    /// instance whose <c>Sfu</c> was null. Accepting a call threw a null reference and answered 500.
    /// Bots could be called and could never answer.</para>
    ///
    /// <para>Enqueued at the same depth rather than one deeper: the convention is not a call, it is
    /// the framework adopting the type, so the family's own code gets the same budget the feature's
    /// does instead of arriving pre-spent.</para>
    /// </remarks>
    private IEnumerable<Type> AdoptedByConvention(MethodBase method)
    {
        foreach (var callee in ResolveCalls(method))
        foreach (var (name, marker) in ReflectionConventions.Roots)
            if (callee.Name == name)
                foreach (var type in conventionCache.GetOrAdd(marker,
                             m => ReflectionConventions.ImplementorsOf(concreteTypes.Value, m)))
                    yield return type;
    }

    private Type[] Usages(MethodBase method)
        => cache.GetOrAdd(method, m =>
        {
            var result = new List<Type>();

            // The method's own signature. This is what catches a handler mapped by method group: the
            // call that maps it names a delegate, and only the parameters name the settings.
            foreach (var parameter in m.GetParameters())
                Collect(parameter.ParameterType, result);

            // Type arguments only -- this is GetRequiredService<IOptions<T>>() and friends. The
            // callees' own parameters are deliberately not read here: an Argon callee is walked in
            // its own right and its signature is collected when it is, while a framework one takes
            // whatever the framework takes and none of it is a section a feature declares. Reading
            // them here instead attributed anything an overload nearby happened to accept to the
            // feature that made the call.
            foreach (var callee in ResolveCalls(m))
                if (callee is MethodInfo { IsGenericMethod: true } generic && !Optional(generic))
                    foreach (var argument in generic.GetGenericArguments())
                        Collect(argument, result);

            return [.. result.Distinct()];
        });

    /// <summary>
    /// Asking in a way that copes with the answer being absent.
    /// </summary>
    /// <remarks>
    /// <para><c>GetService</c> returns null and obliges the caller to handle it, which is a different
    /// statement from a constructor parameter or <c>GetRequiredService</c>: those cannot run without
    /// the section, so a role that does not declare it is broken. This one says the opposite — it is
    /// how a feature asks whether some <i>other</i> role's settings happen to be present, and the
    /// host hooks do exactly that to find out whether the role they are on serves a site at
    /// <c>/</c>.</para>
    ///
    /// <para>Without this distinction the check reports that shape too, and a finding that is
    /// correct-looking and wrong is how a check stops being read.</para>
    /// </remarks>
    private static bool Optional(MethodInfo method)
        => method.Name is "GetService" or "GetKeyedService";

    /// <summary>
    /// Unwraps <c>IOptions&lt;T&gt;</c> and keeps <c>T</c> when it is one of ours.
    /// </summary>
    /// <remarks>
    /// Restricted to the product's own assemblies because the framework asks for its own options
    /// everywhere — <c>IOptions&lt;CookieAuthenticationOptions&gt;</c> and a hundred like it — and
    /// none of those are sections a feature declares.
    /// </remarks>
    private void Collect(Type type, List<Type> into)
    {
        if (!type.IsGenericType || !Accessors.Contains(type.GetGenericTypeDefinition().Name))
            return;

        var settings = type.GetGenericArguments()[0];

        if (settings is { IsClass: true, ContainsGenericParameters: false } && scanned.Contains(settings.Assembly))
            into.Add(settings);
    }

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
