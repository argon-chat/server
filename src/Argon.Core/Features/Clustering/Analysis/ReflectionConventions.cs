namespace Argon.Features.Clustering;

/// <summary>
/// Calls that hand a framework a whole <i>family</i> of types rather than naming any of them.
/// </summary>
/// <remarks>
/// <para><c>AddControllers()</c> gives MVC every <c>ControllerBase</c> it can find; <c>MapBotApi()</c>
/// does the same for the bot interfaces, scanning loaded assemblies and building each one through
/// <c>ActivatorUtilities</c>. Nothing in the IL names the types that come back, so a scanner reading
/// only what is written down sees a feature that registers nothing and depends on nothing — while at
/// run time it has just taken on every constructor in that family.</para>
///
/// <para>One table, read by every scanner that walks a feature, because the two of them asking
/// different questions of the same convention is how they end up disagreeing about it. The bot API
/// was in <see cref="ServiceRegistrationScanner"/>'s copy and not in the options scanner's, and the
/// gap was exactly the size of that difference: accepting a call read <c>CallKitOptions</c> from a
/// constructor no walk reached, on a role that never declared the section, and answered 500 on a
/// null reference the first time a bot was called.</para>
/// </remarks>
internal static class ReflectionConventions
{
    public static readonly (string Method, string MarkerType)[] Roots =
    [
        ("AddControllers", "Microsoft.AspNetCore.Mvc.ControllerBase"),
        ("MapBotApi",      "Argon.Features.BotApi.IBotInterface")
    ];

    /// <summary>
    /// Concrete scanned types assignable to a marker named by its full name.
    /// </summary>
    /// <remarks>
    /// By name rather than by <c>typeof</c> because the markers live in assemblies this one does not
    /// reference — MVC's, and the bot API's — and a scanner that had to reference everything it can
    /// describe would be a dependency on the whole product.
    /// </remarks>
    public static Type[] ImplementorsOf(IReadOnlyList<Type> concreteTypes, string markerTypeName)
    {
        var marker = concreteTypes
           .SelectMany(t => t.GetInterfaces().Cast<Type>().Append(t))
           .FirstOrDefault(t => t.FullName == markerTypeName)
                  ?? concreteTypes.Select(t => t.BaseType).FirstOrDefault(t => t?.FullName == markerTypeName);

        return marker is null
            ? []
            : concreteTypes.Where(t => t != marker && marker.IsAssignableFrom(t)).ToArray();
    }
}
