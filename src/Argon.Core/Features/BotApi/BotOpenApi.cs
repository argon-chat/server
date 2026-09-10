namespace Argon.Features.BotApi;

using Argon.Features.BotApi.Contracts;
using Microsoft.AspNetCore.Http.Metadata;
using Microsoft.AspNetCore.Mvc.ApiExplorer;
using Microsoft.AspNetCore.OpenApi;
using System.Text.Json.Nodes;
using Microsoft.OpenApi;

/// <summary>
/// The OpenAPI description of the Bot API, generated from the endpoints that are actually mapped.
/// <para>
/// Nothing here declares routes: it reads <see cref="BotInterfaceMetadata"/> and
/// <see cref="BotOperationMetadata"/> off the endpoints, so a route that was renamed, dropped or
/// given a different response type is described as it now is, not as someone remembered to say.
/// </para>
/// </summary>
public static class BotOpenApi
{
    public const string DocumentName = "bot";

    /// <summary>Where the running server serves the document.</summary>
    public const string RoutePath = "/api/bot/openapi.json";

    /// <summary>
    /// Version of the description itself. Deliberately a constant rather than the build version:
    /// the document is committed and diffed, and a version that moves on every build would make
    /// every diff noise.
    /// </summary>
    private const string DocumentVersion = "1.0.0";

    /// <summary>The prefix the public gateway strips; see <see cref="GatewayUrl"/>.</summary>
    private const string RoutePrefix = "/api/bot";

    private const string GatewayUrl         = "https://gateway.argon.zone";
    private const string SecuritySchemeName = "BotToken";

    public static IServiceCollection AddBotOpenApi(this IServiceCollection services)
    {
        services.AddEndpointsApiExplorer();
        services.AddOpenApi(DocumentName, options =>
        {
            options.ShouldInclude = IsBotEndpoint;
            options.AddDocumentTransformer(TransformDocumentAsync);
            options.AddOperationTransformer(TransformOperationAsync);
            options.AddSchemaTransformer(TransformSchemaAsync);
        });
        return services;
    }

    /// <summary>Serves the document from the running server, unauthenticated like the interface index.</summary>
    public static WebApplication MapBotOpenApi(this WebApplication app)
    {
        app.MapGet(RoutePath, async (HttpContext http, CancellationToken ct) =>
            {
                http.Response.ContentType = "application/json; charset=utf-8";
                await http.Response.WriteAsync(await GenerateAsync(http.RequestServices, ct), ct);
            })
           .AllowAnonymous()
           .ExcludeFromDescription();

        return app;
    }

    /// <summary>Renders the document for the endpoints mapped in <paramref name="services"/>.</summary>
    public static async Task<string> GenerateAsync(IServiceProvider services, CancellationToken ct = default)
    {
        var provider = services.GetKeyedService<IOpenApiDocumentProvider>(DocumentName)
                    ?? services.GetRequiredService<IOpenApiDocumentProvider>();

        return Serialize(await provider.GetOpenApiDocumentAsync(ct));
    }

    /// <summary>
    /// Renders the document without a configured cluster, for the CLI that writes it to the docs
    /// site. Maps the same interfaces through the same <see cref="BotApiRegistration.MapBotApi"/>,
    /// so what is written is what would be served.
    /// </summary>
    internal static async Task<string> GenerateOfflineAsync(CancellationToken ct = default)
    {
        var builder = WebApplication.CreateSlimBuilder();
        builder.Logging.ClearProviders();
        builder.WebHost.UseUrls("http://127.0.0.1:0");

        builder.Services.AddBotApiJson();
        builder.Services.AddBotOpenApi();
        builder.Services.AddBotRateLimiting(new BotRateLimitOptions());

        var app = builder.Build();
        app.MapBotApi(BotApiMapMode.DocumentationOnly);

        // Started, not served: the endpoint data sources are only observable once the host is
        // running, and no request is ever made against them.
        await app.StartAsync(ct);
        try
        {
            return await GenerateAsync(app.Services, ct);
        }
        finally
        {
            await app.StopAsync(ct);
        }
    }

    private static string Serialize(OpenApiDocument document)
    {
        // "\n" regardless of host OS: the document is committed, and CRLF would make it differ
        // between a Windows developer and CI for no reason.
        var text   = new StringWriter { NewLine = "\n" };
        var writer = new OpenApiJsonWriter(text);

        document.SerializeAs(OpenApiSpecVersion.OpenApi3_0, writer);
        return text.ToString();
    }

    /// <summary>
    /// An endpoint belongs to the Bot API when it carries the interface metadata that
    /// <see cref="BotApiRegistration.MapBotApi"/> attaches — not when its URL happens to start with
    /// the right prefix.
    /// </summary>
    private static bool IsBotEndpoint(ApiDescription description)
        => description.ActionDescriptor.EndpointMetadata.OfType<BotInterfaceMetadata>().Any();

    private static Task TransformDocumentAsync(OpenApiDocument document, OpenApiDocumentTransformerContext context, CancellationToken ct)
    {
        document.Info = new OpenApiInfo
        {
            Title       = "Argon Bot API",
            Version     = DocumentVersion,
            Description =
                "HTTP API for Argon bots. Every request carries a bot token as `Authorization: Bot <token>`; "
              + "the token may also be given as the first path segment. Interfaces are versioned independently, "
              + "so `/IMessages/v1/Send` and `/IMessages/v2/Send` can both exist."
        };

        document.Servers = [new OpenApiServer { Url = GatewayUrl, Description = "Argon bot gateway" }];

        document.Components ??= new OpenApiComponents();
        document.Components.SecuritySchemes ??= new Dictionary<string, IOpenApiSecurityScheme>();
        document.Components.SecuritySchemes[SecuritySchemeName] = new OpenApiSecurityScheme
        {
            Type        = SecuritySchemeType.ApiKey,
            In          = ParameterLocation.Header,
            Name        = "Authorization",
            Description = "Bot token, sent as `Authorization: Bot <token>`."
        };

        document.Security =
        [
            new OpenApiSecurityRequirement
            {
                [new OpenApiSecuritySchemeReference(SecuritySchemeName, document)] = []
            }
        ];

        document.Tags = CollectTags(context);
        document.Paths = StripRoutePrefix(document.Paths);

        return Task.CompletedTask;
    }

    /// <summary>One tag per interface, described by its <see cref="BotDescriptionAttribute"/>.</summary>
    private static HashSet<OpenApiTag> CollectTags(OpenApiDocumentTransformerContext context)
    {
        var byName = new SortedDictionary<string, OpenApiTag>(StringComparer.Ordinal);

        foreach (var metadata in context.DescriptionGroups
                    .SelectMany(g => g.Items)
                    .SelectMany(d => d.ActionDescriptor.EndpointMetadata.OfType<BotInterfaceMetadata>()))
        {
            if (byName.ContainsKey(metadata.Name))
                continue;

            byName[metadata.Name] = new OpenApiTag
            {
                Name        = metadata.Name,
                Description = metadata.Description
            };
        }

        return [..byName.Values];
    }

    /// <summary>
    /// Routes are mapped under <c>/api/bot</c>, but the public gateway serves them at the root of
    /// <see cref="GatewayUrl"/>. The document describes what a client calls.
    /// </summary>
    private static OpenApiPaths StripRoutePrefix(OpenApiPaths? paths)
    {
        var result = new OpenApiPaths();

        foreach (var (path, item) in paths ?? [])
            result[path.StartsWith(RoutePrefix, StringComparison.Ordinal) ? path[RoutePrefix.Length..] : path] = item;

        return result;
    }

    private static Task TransformOperationAsync(OpenApiOperation operation, OpenApiOperationTransformerContext context, CancellationToken ct)
    {
        var metadata = context.Description.ActionDescriptor.EndpointMetadata;
        var iface    = metadata.OfType<BotInterfaceMetadata>().LastOrDefault();

        if (iface is null)
            return Task.CompletedTask;

        var route = metadata.OfType<BotOperationMetadata>().LastOrDefault();

        operation.Tags       = new HashSet<OpenApiTagReference> { new(iface.Name, context.Document) };
        operation.Deprecated = iface.SunsetDate is not null;
        operation.OperationId = $"{iface.Name}_v{iface.Version}_{MethodName(route, context.Description)}";

        if (route is not null)
        {
            operation.Summary     = route.Summary;
            operation.Description = Describe(route, iface);
            DescribeErrors(operation, route.Errors);
            Annotate(operation, route);
        }

        DescribeErrors(operation, [BotErrors.Unauthorized, BotErrors.RateLimited]);
        CamelCaseQueryParameters(operation);
        return Task.CompletedTask;
    }

    /// <summary>
    /// Query binding is case-insensitive, but the document should read the way the JSON bodies do —
    /// and the way every existing route already spells its parameters.
    /// </summary>
    private static void CamelCaseQueryParameters(OpenApiOperation operation)
    {
        foreach (var parameter in operation.Parameters ?? [])
        {
            if (parameter is not OpenApiParameter { In: ParameterLocation.Query, Name.Length: > 0 } query)
                continue;

            query.Name = char.ToLowerInvariant(query.Name[0]) + query.Name[1..];
        }
    }

    /// <summary>
    /// Restates the permission and the error list as extensions, so the documentation site can read
    /// them as data rather than parsing them back out of the prose. Client generators ignore
    /// <c>x-</c> keys, so this costs the generated clients nothing.
    /// </summary>
    private static void Annotate(OpenApiOperation operation, BotOperationMetadata route)
    {
        operation.Extensions ??= new Dictionary<string, IOpenApiExtension>();

        if (route.Permission is { } permission)
            operation.Extensions["x-argon-permission"] = new JsonNodeExtension(permission.ToString());

        if (route.IsPrivileged)
            operation.Extensions["x-argon-privileged"] = new JsonNodeExtension(true);

        if (route.Errors.Count == 0)
            return;

        var errors = new JsonArray();
        foreach (var error in route.Errors.DistinctBy(e => (e.Status, e.Code)).OrderBy(e => e.Status).ThenBy(e => e.Code, StringComparer.Ordinal))
            errors.Add(new JsonObject
            {
                ["status"]      = error.Status,
                ["code"]        = error.Code,
                ["description"] = error.Description
            });

        operation.Extensions["x-argon-errors"] = new JsonNodeExtension(errors);
    }

    /// <summary>The trailing segment of the route — <c>Send</c> in <c>/IMessages/v1/Send</c>.</summary>
    private static string MethodName(BotOperationMetadata? route, ApiDescription description)
    {
        var path = route?.Path ?? description.RelativePath ?? string.Empty;
        var last = path.TrimEnd('/').LastIndexOf('/');

        return last >= 0 ? path[(last + 1)..] : path;
    }

    /// <summary>
    /// What the summary does not already say. The summary is rendered next to it everywhere, so
    /// repeating it here would only make both harder to read.
    /// </summary>
    private static string? Describe(BotOperationMetadata route, BotInterfaceMetadata iface)
    {
        var parts = new List<string>();

        if (route.Permission is { } permission)
            parts.Add($"Requires the `{permission}` entitlement in the target space.");

        if (route.IsPrivileged)
            parts.Add("Requires a privileged intent, which a bot has to be granted.");

        if (iface.SunsetDate is { } sunset)
            parts.Add($"Deprecated: this interface version is removed on {sunset:yyyy-MM-dd}.");

        return parts.Count > 0 ? string.Join("\n\n", parts) : null;
    }

    /// <summary>
    /// Names the error codes behind each status. The status itself is already on the operation —
    /// <see cref="BotRouteSpec{TRequest,TResponse}.Throws"/> declares it as a response — so this
    /// only fills in what a client switches on.
    /// </summary>
    private static void DescribeErrors(OpenApiOperation operation, IReadOnlyList<BotError> errors)
    {
        if (operation.Responses is null || errors.Count == 0)
            return;

        foreach (var group in errors.GroupBy(e => e.Status))
        {
            if (!operation.Responses.TryGetValue(group.Key.ToString(), out var existing) || existing is not OpenApiResponse response)
                continue;

            response.Description = string.Join("\n", group
               .DistinctBy(e => e.Code)
               .OrderBy(e => e.Code, StringComparer.Ordinal)
               .Select(e => $"`{e.Code}` — {e.Description}"));
        }
    }

    /// <summary>
    /// Ion discriminated unions reach the wire through a custom converter, so the schema generator
    /// has nothing to describe them with and emits an array with no item schema — which no client
    /// generator accepts. Describe them as a <c>oneOf</c> over the concrete variants instead.
    /// </summary>
    private static async Task TransformSchemaAsync(OpenApiSchema schema, OpenApiSchemaTransformerContext context, CancellationToken ct)
    {
        var type = context.JsonTypeInfo.Type;

        if (IsIonUnion(type))
        {
            await FillUnionAsync(schema, type, context, ct);
            return;
        }

        if (schema.Items is null && ElementType(type) is { } element && IsIonUnion(element))
        {
            var items = new OpenApiSchema();
            await FillUnionAsync(items, element, context, ct);
            schema.Items = items;
        }
    }

    private static async Task FillUnionAsync(OpenApiSchema schema, Type union, OpenApiSchemaTransformerContext context, CancellationToken ct)
    {
        var variants = UnionVariants(union);
        if (variants.Count == 0)
            return;

        schema.Type  = null;
        schema.OneOf = [];

        foreach (var variant in variants)
            schema.OneOf.Add(await context.GetOrCreateSchemaAsync(variant, null, ct));
    }

    private static bool IsIonUnion(Type type)
        => type.IsInterface && type.GetInterfaces()
           .Any(i => i.IsGenericType && i.GetGenericTypeDefinition().Name.StartsWith("IIonUnion", StringComparison.Ordinal));

    private static List<Type> UnionVariants(Type union)
        => BotApiRegistration.ArgonAssemblies()
           .SelectMany(a =>
            {
                try
                {
                    return a.GetTypes();
                }
                catch (ReflectionTypeLoadException e)
                {
                    return e.Types.OfType<Type>().ToArray();
                }
            })
           .Where(t => t is { IsClass: true, IsAbstract: false } && union.IsAssignableFrom(t))
           .OrderBy(t => t.Name, StringComparer.Ordinal)
           .ToList();

    private static Type? ElementType(Type type)
    {
        if (type.IsArray)
            return type.GetElementType();

        return type.GetInterfaces().Append(type)
           .FirstOrDefault(i => i.IsGenericType && i.GetGenericTypeDefinition() == typeof(IEnumerable<>))
          ?.GetGenericArguments()[0];
    }

    /// <summary>
    /// The responses every bot endpoint can produce, from the token check and the rate limiter that
    /// sit in front of all of them. Attached to the whole group so no route has to remember them.
    /// </summary>
    internal static IEnumerable<object> UniversalResponseMetadata()
    {
        yield return new ProducesResponseTypeMetadata(
            StatusCodes.Status401Unauthorized, typeof(BotApiError), ["application/json"]);
        yield return new ProducesResponseTypeMetadata(
            StatusCodes.Status429TooManyRequests, typeof(BotApiError), ["application/json"]);
    }
}
