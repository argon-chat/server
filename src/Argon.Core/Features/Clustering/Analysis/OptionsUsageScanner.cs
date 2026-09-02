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

            if (depth >= MaxDepth)
                continue;

            foreach (var callee in Callees(method))
                if (visited.Add(callee))
                    queue.Enqueue((callee, depth + 1));
        }

        return found;
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
