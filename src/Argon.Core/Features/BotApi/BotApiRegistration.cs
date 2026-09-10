namespace Argon.Features.BotApi;

using System.Runtime.CompilerServices;
using Microsoft.AspNetCore.Routing;
using Microsoft.Extensions.DependencyInjection;

/// <summary>Why the Bot API is being mapped.</summary>
internal enum BotApiMapMode
{
    /// <summary>Normal hosting: authorization, real dependencies, handlers that run.</summary>
    Serving,

    /// <summary>
    /// Offline OpenAPI generation: the endpoints exist so their metadata can be read, and the
    /// request pipeline they would need is never built.
    /// </summary>
    DocumentationOnly
}

/// <summary>
/// Marks a class as a Bot API interface with Steam-like per-interface versioning.
/// Route pattern: /api/bot/{InterfaceName}/v{Version}/{Method}
/// </summary>
[AttributeUsage(AttributeTargets.Class, AllowMultiple = false)]
public sealed class BotInterfaceAttribute(string name, int version) : Attribute
{
    public string Name    { get; } = name;
    public int    Version { get; } = version;
}

/// <summary>
/// Marks a bot interface version as deprecated with a sunset date.
/// </summary>
[AttributeUsage(AttributeTargets.Class, AllowMultiple = false)]
public sealed class BotInterfaceDeprecatedAttribute(string sunsetDate) : Attribute
{
    public DateTimeOffset SunsetDate { get; } = DateTimeOffset.Parse(sunsetDate);
}

/// <summary>
/// Bot API interface contract. Each implementation is a versioned interface
/// (e.g. IMessages/v1, IChannels/v2) that maps its routes onto a RouteGroupBuilder.
/// </summary>
public interface IBotInterface
{
    void MapRoutes(RouteGroupBuilder group);
}

public sealed record BotInterfaceInfo(
    string Name,
    int    Version,
    bool   IsDeprecated,
    DateTimeOffset? SunsetDate);

public static class BotApiRegistration
{
    /// <summary>
    /// Registers the STJ converter for IMessageEntity so Minimal API endpoints
    /// can deserialize polymorphic entities from request bodies.
    /// </summary>
    public static IServiceCollection AddBotApiJson(this IServiceCollection services)
    {
        services.ConfigureHttpJsonOptions(opts =>
        {
            opts.SerializerOptions.Converters.Add(new MessageEntityStjConverter());
        });
        return services;
    }

    public static WebApplication MapBotApi(this WebApplication app)
        => app.MapBotApi(BotApiMapMode.Serving);

    internal static WebApplication MapBotApi(this WebApplication app, BotApiMapMode mode)
    {
        var botGroup = app.MapGroup("/api/bot");

        if (mode is BotApiMapMode.Serving)
            botGroup.RequireAuthorization(new Microsoft.AspNetCore.Authorization.AuthorizeAttribute
            {
                AuthenticationSchemes = BotTokenAuthenticationHandler.SchemeName
            });

        // The token check and the rate limiter sit in front of every bot route, so the responses
        // they produce belong to every bot route.
        foreach (var metadata in BotOpenApi.UniversalResponseMetadata())
            botGroup.WithMetadata(metadata);

        var interfaces  = DiscoverInterfaces();
        var metadataMap = new List<BotInterfaceInfo>();

        foreach (var (type, attr, deprecated) in interfaces)
        {
            var interfaceGroup = botGroup.MapGroup($"/{attr.Name}/v{attr.Version}");

            // Marks every endpoint below as part of this interface: the OpenAPI document is built
            // from endpoints carrying this, not from a parallel list of attributes.
            interfaceGroup.WithMetadata(new BotInterfaceMetadata(
                attr.Name,
                attr.Version,
                type.GetCustomAttribute<BotDescriptionAttribute>()?.Description,
                deprecated?.SunsetDate));

            // Add deprecation headers via endpoint filter
            if (deprecated is not null)
            {
                interfaceGroup.AddEndpointFilter(async (ctx, next) =>
                {
                    ctx.HttpContext.Response.Headers["Sunset"]      = deprecated.SunsetDate.ToString("R");
                    ctx.HttpContext.Response.Headers["Deprecation"]  = "true";
                    return await next(ctx);
                });
            }

            CreateInterface(app.Services, type, mode).MapRoutes(interfaceGroup);

            metadataMap.Add(new BotInterfaceInfo(
                attr.Name,
                attr.Version,
                deprecated is not null,
                deprecated?.SunsetDate));
        }

        // Metadata endpoint — list all available interfaces and versions
        botGroup.MapGet("/", (HttpContext _) =>
        {
            var grouped = metadataMap
               .GroupBy(x => x.Name)
               .Select(g => new
                {
                    name           = g.Key,
                    latestVersion  = g.Where(x => !x.IsDeprecated).Max(x => x.Version),
                    versions       = g.OrderByDescending(x => x.Version).Select(x => new
                    {
                        version      = x.Version,
                        isDeprecated = x.IsDeprecated,
                        sunsetDate   = x.SunsetDate?.ToString("O"),
                        url          = $"/api/bot/{g.Key}/v{x.Version}"
                    })
                });

            return Results.Ok(new { interfaces = grouped });
        }).AllowAnonymous();

        return app;
    }

    /// <summary>
    /// Builds the interface so its routes can be mapped. When only the route metadata is wanted the
    /// dependencies are not: OpenAPI generation runs outside a configured cluster, so the instance
    /// is left uninitialised — <see cref="IBotInterface.MapRoutes"/> only closes over the
    /// dependencies for handlers that never run in that mode.
    /// </summary>
    private static IBotInterface CreateInterface(IServiceProvider services, Type type, BotApiMapMode mode)
    {
        if (mode is BotApiMapMode.Serving)
            return (IBotInterface)ActivatorUtilities.CreateInstance(services, type);

        try
        {
            return (IBotInterface)ActivatorUtilities.CreateInstance(services, type);
        }
        catch (InvalidOperationException)
        {
            return (IBotInterface)RuntimeHelpers.GetUninitializedObject(type);
        }
    }

    /// <summary>
    /// Every Argon assembly, loaded rather than merely whatever happened to be loaded already.
    /// </summary>
    /// <remarks>
    /// <para><c>AppDomain.CurrentDomain.GetAssemblies()</c> answers with what the runtime has loaded
    /// so far, and .NET loads lazily — an assembly nothing has touched yet is simply not in the
    /// list. The bot interfaces live in <c>Argon.Api</c>, so a caller that had not yet reached into
    /// it discovers no interfaces and gets no error, which is indistinguishable from an API that
    /// has no routes.</para>
    ///
    /// <para>That matters more now than it did: this scan is what the OpenAPI document — and so the
    /// published description of the API — is built from.</para>
    /// </remarks>
    internal static IEnumerable<Assembly> ArgonAssemblies()
    {
        var seen  = new HashSet<string>(StringComparer.Ordinal);
        var queue = new Queue<Assembly>(AppDomain.CurrentDomain.GetAssemblies()
           .Where(a => a.FullName?.StartsWith("Argon") == true));

        foreach (var start in new[] { typeof(BotApiRegistration).Assembly, Assembly.GetEntryAssembly() })
            if (start is not null)
                queue.Enqueue(start);

        while (queue.Count > 0)
        {
            var assembly = queue.Dequeue();
            if (assembly.FullName is not { } name || !seen.Add(name))
                continue;

            if (name.StartsWith("Argon"))
                yield return assembly;

            foreach (var reference in assembly.GetReferencedAssemblies())
            {
                if (!reference.FullName.StartsWith("Argon") || seen.Contains(reference.FullName))
                    continue;

                // A reference that cannot be resolved is not this method's problem to report: the
                // scan is a best effort over what the deployment actually ships.
                try
                {
                    queue.Enqueue(Assembly.Load(reference));
                }
                catch (Exception)
                {
                    // ignored — an unresolvable Argon reference simply contributes no types
                }
            }
        }
    }

    private static List<(Type Type, BotInterfaceAttribute Attr, BotInterfaceDeprecatedAttribute? Deprecated)> DiscoverInterfaces()
    {
        var result = new List<(Type, BotInterfaceAttribute, BotInterfaceDeprecatedAttribute?)>();

        foreach (var assembly in ArgonAssemblies())
        {
            foreach (var type in assembly.GetTypes())
            {
                if (!typeof(IBotInterface).IsAssignableFrom(type) || type.IsAbstract || type.IsInterface)
                    continue;

                var attr = type.GetCustomAttribute<BotInterfaceAttribute>();
                if (attr is null)
                    continue;

                var deprecated = type.GetCustomAttribute<BotInterfaceDeprecatedAttribute>();
                result.Add((type, attr, deprecated));
            }
        }

        return result;
    }
}
