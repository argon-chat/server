namespace Argon.Features.BotApi;

using Argon.Features.BotApi.Contracts;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Routing;

/// <summary>
/// One failure mode of a bot route: the HTTP status, the machine-readable value that lands in the
/// <c>error</c> field of <see cref="BotApiError"/>, and what it means.
/// <para>
/// A route declares the errors it can produce with <c>Throws</c>, and a handler returns one by
/// throwing <em>the same object</em> — see <see cref="Raise"/>. There is no second place to keep
/// in step: what the documentation promises is what the code throws.
/// </para>
/// </summary>
public sealed record BotError(int Status, string Code, string Description)
{
    /// <summary>
    /// Builds the exception that returns this error to the caller:
    /// <c>throw BotErrors.NotAMember.Raise();</c>
    /// </summary>
    /// <param name="message">
    /// Overrides the human-readable message for this occurrence. The <see cref="Code"/> a client
    /// switches on stays the same either way.
    /// </param>
    public BotApiException Raise(string? message = null) => new(this, message);
}

/// <summary>
/// Thrown by a bot route handler to return a declared <see cref="BotError"/>.
/// <see cref="BotErrorFilter"/> turns it into the JSON error body.
/// </summary>
public sealed class BotApiException(BotError error, string? message = null)
    : Exception(message ?? error.Description)
{
    public BotError Error { get; } = error;
}

/// <summary>Errors more than one interface can produce.</summary>
public static class BotErrors
{
    public static readonly BotError NotAMember     = new(403, "not_a_member", "Bot is not a member of this space.");
    public static readonly BotError MissingSpaceId = new(400, "missing_space_id", "spaceId is required.");
    public static readonly BotError NotVerified    = new(403, "not_verified", "This endpoint requires a verified bot.");
    public static readonly BotError NotFound       = new(404, "not_found", "The requested entity does not exist.");

    /// <summary>Produced by the rate limiter, not by handlers — every bot route can return it.</summary>
    public static readonly BotError RateLimited = new(429, "rate_limited", "You are being rate limited.");

    /// <summary>Produced by the authentication handler — every bot route can return it.</summary>
    public static readonly BotError Unauthorized = new(401, "unauthorized", "Missing, malformed or revoked bot token.");
}

/// <summary>
/// Identifies the interface an endpoint belongs to. Attached to the whole
/// <c>/{Interface}/v{Version}</c> group by <see cref="BotApiRegistration.MapBotApi"/>, which is also
/// what marks an endpoint as part of the Bot API for OpenAPI generation.
/// </summary>
public sealed record BotInterfaceMetadata(
    string          Name,
    int             Version,
    string?         Description,
    DateTimeOffset? SunsetDate);

/// <summary>
/// Everything the documentation needs about one route, attached to the endpoint by the route
/// builder itself. Unlike an attribute, it cannot describe a route that was never mapped.
/// </summary>
public sealed record BotOperationMetadata(
    string                  Method,
    string                  Path,
    string?                 Summary,
    ArgonEntitlement?       Permission,
    bool                    IsPrivileged,
    IReadOnlyList<BotError> Errors);

/// <summary>
/// Turns <see cref="BotApiException"/> into the JSON error body, and complains when a route returns
/// an error it never declared — the drift that used to be invisible.
/// </summary>
public sealed class BotErrorFilter(ILogger<BotErrorFilter> logger) : IEndpointFilter
{
    public async ValueTask<object?> InvokeAsync(EndpointFilterInvocationContext context, EndpointFilterDelegate next)
    {
        try
        {
            return await next(context);
        }
        catch (BotApiException ex)
        {
            var route = context.HttpContext.GetEndpoint()?.Metadata.GetMetadata<BotOperationMetadata>();

            if (route is not null && !route.Errors.Any(e => e.Code == ex.Error.Code && e.Status == ex.Error.Status))
                logger.LogWarning(
                    "Bot route {Method} {Path} returned undeclared error {Status} {Code}. " +
                    "It is missing from the OpenAPI document — add .Throws() to the route definition",
                    route.Method, route.Path, ex.Error.Status, ex.Error.Code);

            return Results.Json(new BotApiError(ex.Error.Code, ex.Message), statusCode: ex.Error.Status);
        }
    }
}

/// <summary>
/// Refuses a route to bots that have not been verified. Installed by
/// <c>RequiresVerifiedBot()</c>, which documents the 403 in the same call.
/// </summary>
public sealed class BotVerifiedFilter : IEndpointFilter
{
    public async ValueTask<object?> InvokeAsync(EndpointFilterInvocationContext context, EndpointFilterDelegate next)
    {
        if (!context.HttpContext.GetBotIsVerified())
            throw BotErrors.NotVerified.Raise();

        return await next(context);
    }
}

/// <summary>How the request payload of a route reaches the handler.</summary>
public enum BotRequestBinding
{
    /// <summary>Deserialized from the JSON request body.</summary>
    Body,

    /// <summary>Bound from the query string, one property per parameter.</summary>
    Query
}

internal sealed class BotRouteConfig(RouteGroupBuilder group, string method, string path, BotRequestBinding binding)
{
    public RouteGroupBuilder Group  { get; } = group;
    public string            Method { get; } = method;
    public string            Path   { get; } = path;

    public BotRequestBinding Binding              { get; set; } = binding;
    public string            ContentType          { get; set; } = "application/json";
    public string?           Summary              { get; set; }
    public ArgonEntitlement? Permission           { get; set; }
    public bool              IsPrivileged         { get; set; }
    public bool              NeedsSpaceMembership { get; set; }
    public bool              NeedsVerifiedBot     { get; set; }
    public List<BotError>    Errors               { get; } = [];

    public RouteHandlerBuilder Apply(RouteHandlerBuilder builder, Type? responseType)
    {
        // Registration order is invocation order, so the error filter goes on first: it has to wrap
        // the membership and verification filters to translate what they throw.
        builder.AddEndpointFilter<BotErrorFilter>();

        if (NeedsVerifiedBot)
            builder.AddEndpointFilter<BotVerifiedFilter>();

        if (NeedsSpaceMembership)
            builder.AddEndpointFilter<BotSpaceMembershipFilter>();

        builder.WithMetadata(new BotOperationMetadata(Method, Path, Summary, Permission, IsPrivileged, Errors));

        if (Summary is not null)
            builder.WithMetadata(new EndpointSummaryAttribute(Summary));

        if (responseType is null)
            builder.Produces(StatusCodes.Status200OK);
        else
            builder.Produces(StatusCodes.Status200OK, responseType, ContentType);

        foreach (var status in Errors.Select(e => e.Status).Distinct())
            builder.Produces<BotApiError>(status, "application/json");

        return builder;
    }
}

/// <summary>
/// What every bot route can say about itself, whatever shape its request and response take.
/// <typeparamref name="TSelf"/> exists only so the fluent chain keeps its concrete type.
/// </summary>
public abstract class BotRouteSpecBase<TSelf> where TSelf : BotRouteSpecBase<TSelf>
{
    private protected readonly BotRouteConfig cfg;

    private protected BotRouteSpecBase(BotRouteConfig cfg) => this.cfg = cfg;

    private TSelf Self => (TSelf)this;

    /// <summary>One line on what the route does, shown as the OpenAPI operation summary.</summary>
    public TSelf Summary(string summary)
    {
        cfg.Summary = summary;
        return Self;
    }

    /// <summary>
    /// The entitlement the acting bot needs in the target space. Declarative for now: enforcement
    /// still lives in the grains the handler calls, so this documents the requirement rather than
    /// imposing it. Typed rather than a string so a renamed entitlement cannot rot here.
    /// </summary>
    public TSelf Permission(ArgonEntitlement entitlement)
    {
        cfg.Permission = entitlement;
        return Self;
    }

    /// <summary>Marks the route as needing a privileged intent, which a bot has to be granted.</summary>
    public TSelf Privileged()
    {
        cfg.IsPrivileged = true;
        return Self;
    }

    /// <summary>Declares an error this route can return. Handlers return it by throwing it.</summary>
    public TSelf Throws(BotError error)
    {
        cfg.Errors.Add(error);
        return Self;
    }

    /// <summary>
    /// Requires the bot to be a member of the space the request names, and declares the responses
    /// that refusal produces. The filter and the documented errors are installed together.
    /// </summary>
    public TSelf RequiresSpaceMembership()
    {
        cfg.NeedsSpaceMembership = true;
        cfg.Errors.Add(BotErrors.NotAMember);
        cfg.Errors.Add(BotErrors.MissingSpaceId);
        return Self;
    }

    /// <summary>Restricts the route to verified bots, and declares the 403 everyone else gets.</summary>
    public TSelf RequiresVerifiedBot()
    {
        cfg.NeedsVerifiedBot = true;
        cfg.Errors.Add(BotErrors.NotVerified);
        return Self;
    }

    /// <summary>Binds the request from the query string rather than the body.</summary>
    public TSelf FromQuery()
    {
        cfg.Binding = BotRequestBinding.Query;
        return Self;
    }

    /// <summary>Binds the request from the JSON body rather than the query string.</summary>
    public TSelf FromBody()
    {
        cfg.Binding = BotRequestBinding.Body;
        return Self;
    }

    /// <summary>The media type of a successful response, for routes that do not answer in JSON.</summary>
    public TSelf Produces(string contentType)
    {
        cfg.ContentType = contentType;
        return Self;
    }
}

/// <summary>
/// A route that takes a request payload and answers with one.
/// Call <c>Handle</c> last: it is what actually maps the endpoint.
/// </summary>
public sealed class BotRouteSpec<TRequest, TResponse> : BotRouteSpecBase<BotRouteSpec<TRequest, TResponse>>
    where TRequest : notnull
{
    internal BotRouteSpec(BotRouteConfig cfg) : base(cfg) { }

    /// <summary>Maps the endpoint. The response type is the handler's, so it cannot be misdeclared.</summary>
    public RouteHandlerBuilder Handle(Func<HttpContext, TRequest, Task<TResponse>> handler)
        => cfg.Apply(Map(cfg, handler), typeof(TResponse));

    /// <summary>
    /// Maps the endpoint for a response the framework has to build itself — a stream, say. The
    /// declared response type is a promise here rather than a consequence, so prefer <c>Handle</c>.
    /// </summary>
    public RouteHandlerBuilder HandleResult(Func<HttpContext, TRequest, Task<IResult>> handler)
        => cfg.Apply(Map(cfg, handler), typeof(TResponse));

    internal static RouteHandlerBuilder Map<T>(BotRouteConfig cfg, Func<HttpContext, TRequest, Task<T>> handler)
        // [FromBody] rather than letting it be inferred: a DELETE never infers a body, and two
        // routes here take one.
        => cfg.Binding is BotRequestBinding.Body
            ? cfg.Group.MapMethods(cfg.Path, [cfg.Method],
                async (HttpContext ctx, [FromBody] TRequest request) => await handler(ctx, request))
            : cfg.Group.MapMethods(cfg.Path, [cfg.Method],
                async (HttpContext ctx, [AsParameters] TRequest request) => await handler(ctx, request));
}

/// <summary>A route that takes a request payload and answers with an empty 200.</summary>
public sealed class BotCommandSpec<TRequest> : BotRouteSpecBase<BotCommandSpec<TRequest>>
    where TRequest : notnull
{
    internal BotCommandSpec(BotRouteConfig cfg) : base(cfg) { }

    /// <inheritdoc cref="BotRouteSpec{TRequest,TResponse}.Handle"/>
    public RouteHandlerBuilder Handle(Func<HttpContext, TRequest, Task> handler)
        => cfg.Apply(
            BotRouteSpec<TRequest, object>.Map(cfg, async (ctx, request) =>
            {
                await handler(ctx, request);
                return Results.Ok();
            }),
            responseType: null);
}

/// <summary>A route that takes no request payload.</summary>
public sealed class BotRouteSpec<TResponse> : BotRouteSpecBase<BotRouteSpec<TResponse>>
{
    internal BotRouteSpec(BotRouteConfig cfg) : base(cfg) { }

    /// <inheritdoc cref="BotRouteSpec{TRequest,TResponse}.Handle"/>
    public RouteHandlerBuilder Handle(Func<HttpContext, Task<TResponse>> handler)
        => cfg.Apply(cfg.Group.MapMethods(cfg.Path, [cfg.Method], handler), typeof(TResponse));

    /// <inheritdoc cref="BotRouteSpec{TRequest,TResponse}.HandleResult"/>
    public RouteHandlerBuilder HandleResult(Func<HttpContext, Task<IResult>> handler)
        => cfg.Apply(cfg.Group.MapMethods(cfg.Path, [cfg.Method], handler), typeof(TResponse));
}

/// <summary>
/// Typed route registration for <see cref="IBotInterface"/> implementations.
/// <para>
/// The point of going through these rather than <c>MapGet</c>/<c>MapPost</c> directly: the request
/// and response types, the summary, the permission and the error list are stated by the call that
/// maps the endpoint, so the OpenAPI document is derived from the routes that actually exist.
/// </para>
/// <para>
/// A single type argument reads as the payload the verb does not already imply: <c>Get&lt;T&gt;</c>
/// takes nothing and answers with <c>T</c>, <c>Post&lt;T&gt;</c> takes <c>T</c> and answers with an
/// empty 200. <c>GET</c> and <c>DELETE</c> bind their payload from the query string and the rest
/// from the JSON body; <c>FromQuery()</c> and <c>FromBody()</c> override that where a route
/// disagrees.
/// </para>
/// </summary>
public static class BotRouteGroupExtensions
{
    public static BotRouteSpec<TResponse> Get<TResponse>(this RouteGroupBuilder group, string path)
        => new(new BotRouteConfig(group, HttpMethods.Get, path, BotRequestBinding.Query));

    public static BotRouteSpec<TQuery, TResponse> Get<TQuery, TResponse>(this RouteGroupBuilder group, string path)
        where TQuery : notnull
        => new(new BotRouteConfig(group, HttpMethods.Get, path, BotRequestBinding.Query));

    public static BotCommandSpec<TQuery> Delete<TQuery>(this RouteGroupBuilder group, string path)
        where TQuery : notnull
        => new(new BotRouteConfig(group, HttpMethods.Delete, path, BotRequestBinding.Query));

    public static BotRouteSpec<TQuery, TResponse> Delete<TQuery, TResponse>(this RouteGroupBuilder group, string path)
        where TQuery : notnull
        => new(new BotRouteConfig(group, HttpMethods.Delete, path, BotRequestBinding.Query));

    public static BotCommandSpec<TRequest> Post<TRequest>(this RouteGroupBuilder group, string path)
        where TRequest : notnull
        => new(new BotRouteConfig(group, HttpMethods.Post, path, BotRequestBinding.Body));

    public static BotRouteSpec<TRequest, TResponse> Post<TRequest, TResponse>(this RouteGroupBuilder group, string path)
        where TRequest : notnull
        => new(new BotRouteConfig(group, HttpMethods.Post, path, BotRequestBinding.Body));

    public static BotCommandSpec<TRequest> Patch<TRequest>(this RouteGroupBuilder group, string path)
        where TRequest : notnull
        => new(new BotRouteConfig(group, HttpMethods.Patch, path, BotRequestBinding.Body));

    public static BotRouteSpec<TRequest, TResponse> Patch<TRequest, TResponse>(this RouteGroupBuilder group, string path)
        where TRequest : notnull
        => new(new BotRouteConfig(group, HttpMethods.Patch, path, BotRequestBinding.Body));

    public static BotRouteSpec<TRequest, TResponse> Put<TRequest, TResponse>(this RouteGroupBuilder group, string path)
        where TRequest : notnull
        => new(new BotRouteConfig(group, HttpMethods.Put, path, BotRequestBinding.Body));
}
