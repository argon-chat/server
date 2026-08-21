namespace Argon.Features.BotApi;

using Microsoft.AspNetCore.Routing;
using Microsoft.Extensions.DependencyInjection;

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
    {
        var botGroup = app.MapGroup("/api/bot")
           .RequireAuthorization(new Microsoft.AspNetCore.Authorization.AuthorizeAttribute
            {
                AuthenticationSchemes = BotTokenAuthenticationHandler.SchemeName
            });

        var interfaces  = DiscoverInterfaces();
        var metadataMap = new List<BotInterfaceInfo>();

        foreach (var (type, attr, deprecated) in interfaces)
        {
            var interfaceGroup = botGroup.MapGroup($"/{attr.Name}/v{attr.Version}");

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

            var instance = (IBotInterface)ActivatorUtilities.CreateInstance(app.Services, type);
            instance.MapRoutes(interfaceGroup);

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

    private static List<(Type Type, BotInterfaceAttribute Attr, BotInterfaceDeprecatedAttribute? Deprecated)> DiscoverInterfaces()
    {
        var result = new List<(Type, BotInterfaceAttribute, BotInterfaceDeprecatedAttribute?)>();

        // Scan all loaded assemblies for IBotInterface implementations
        var assemblies = AppDomain.CurrentDomain.GetAssemblies()
           .Where(a => a.FullName?.StartsWith("Argon") == true);

        foreach (var assembly in assemblies)
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
