namespace Argon.Features.Integrations.Connections;

using Argon.Features.Jwt;
using Argon.Features.Template;
using Argon.Features.WebSession;
using Argon.Services;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Routing;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;

/// <summary>
/// Where a provider sends the browser back, and the same-site continuation after it.
/// </summary>
/// <remarks>
/// <para>Anonymous, on the entry point, and holding nothing: the whole query and whatever the
/// browser's web session says go to <c>IConnectionHandshakeGrain</c>, and what comes back is a
/// page — including the web client's address for the sign-in link, so this role binds no
/// <c>Connections</c> configuration at all. No client secret, no token and no adapter exists here;
/// a deployment without providers answers every callback with the "not available" page.</para>
///
/// <para>The session cookie is deliberately not read on the callback itself: the redirect is
/// cross-site, and <see cref="WebAccessCookie.Read"/> refuses cross-site requests by design. So the
/// callback parks the exchange and renders a page that continues to <see cref="ResumeRoute"/> on its
/// own — that navigation is same-origin, the cookie is read, and the grain compares the browser's
/// user with the one that started the handshake. That comparison is the login-CSRF gate the
/// contributor coin depends on; see <c>docs/internal/architecture/connections.md</c>.</para>
/// </remarks>
public static class ConnectionsEndpoints
{
    public const string CallbackRoute = "/connections/callback/{provider}";
    public const string ResumeRoute   = "/connections/resume/{handshakeId:guid}";

    /// <summary>The scope a web session is issued with; see <c>WebSessionEndpoints</c>.</summary>
    private const string SessionScope = "argon.app";

    private const string PageForm = "connections_result";

    private const int RequestsPerWindow = 60;
    private const int MaxQueryValues    = 40;
    private const int MaxValueLength    = 4096;

    private static readonly TimeSpan Window = TimeSpan.FromMinutes(5);

    public const string TwitchWebhookRoute = "/connections/webhooks/twitch";

    /// <summary>Twitch's notifications are small; anything past this is not one of them.</summary>
    private const int MaxWebhookBody = 64 * 1024;

    public static WebApplication MapConnections(this WebApplication app)
    {
        app.MapGet(CallbackRoute, CallbackAsync).AllowAnonymous();
        app.MapGet(ResumeRoute, ResumeAsync).AllowAnonymous();
        app.MapPost(TwitchWebhookRoute, TwitchWebhookAsync).AllowAnonymous();

        return app;
    }

    /// <summary>
    /// Twitch EventSub. The five headers and the raw body go to the grain that holds the signing
    /// secret; the answer is whatever it says — the challenge on a verification, 204 on a
    /// notification, 403 on a message that does not verify.
    /// </summary>
    private static async Task<IResult> TwitchWebhookAsync(
        HttpContext       http,
        IClusterClient    cluster,
        ILoggerFactory    loggers,
        CancellationToken ct)
    {
        if (http.Request.ContentLength is > MaxWebhookBody)
            return Results.StatusCode(StatusCodes.Status413PayloadTooLarge);

        string body;

        using (var reader = new StreamReader(http.Request.Body, Encoding.UTF8, leaveOpen: true))
            body = await reader.ReadToEndAsync(ct);

        if (body.Length > MaxWebhookBody)
            return Results.StatusCode(StatusCodes.Status413PayloadTooLarge);

        string Header(string name) => http.Request.Headers.TryGetValue(name, out var values) ? values.ToString() : "";

        var message = new Argon.Features.Integrations.Connections.Twitch.TwitchEventSubMessage(
            Header("Twitch-Eventsub-Message-Id"),
            Header("Twitch-Eventsub-Message-Type"),
            Header("Twitch-Eventsub-Message-Timestamp"),
            Header("Twitch-Eventsub-Message-Signature"),
            Header("Twitch-Eventsub-Subscription-Type"),
            body);

        try
        {
            var answer = await cluster.GetGrain<ITwitchEventSubGrain>(Guid.Empty).HandleAsync(message, ct);

            return answer.Body is null
                ? Results.StatusCode(answer.StatusCode)
                : Results.Content(answer.Body, "text/plain; charset=utf-8", statusCode: answer.StatusCode);
        }
        catch (Exception e)
        {
            // A 5xx makes Twitch retry, which is what is wanted when the cluster could not take it.
            loggers.CreateLogger(typeof(ConnectionsEndpoints)).LogError(e, "An EventSub message could not be handled");
            return Results.StatusCode(StatusCodes.Status503ServiceUnavailable);
        }
    }

    public static string ResumeUrl(Guid handshakeId) => $"/connections/resume/{handshakeId}";

    private static async Task<IResult> CallbackAsync(
        HttpContext                 http,
        string                      provider,
        IClusterClient              cluster,
        IArgonCacheDatabase         cache,
        IOptions<WebSessionOptions> web,
        ClassicJwtFlow              jwt,
        EMailFormStorage            forms,
        ILoggerFactory              loggers,
        CancellationToken           ct)
    {
        if (!ConnectionProviders.TryParse(provider, out var kind))
            return Results.NotFound();

        if (await IsThrottledAsync(cache, http, ct))
            return Results.StatusCode(StatusCodes.Status429TooManyRequests);

        var query         = ReadQuery(http);
        var browserUserId = BrowserUser(http, web.Value, jwt);
        var logger        = loggers.CreateLogger(typeof(ConnectionsEndpoints));

        HandshakePage page;

        try
        {
            page = await cluster.GetGrain<IConnectionHandshakeGrain>(Guid.Empty).CompleteAsync(kind, query, browserUserId, ct);
        }
        catch (Exception e)
        {
            logger.LogError(e, "The {Provider} callback could not be completed", kind);
            page = new HandshakePage(HandshakePageKind.ProviderError, kind, Guid.Empty, null, null, null, ConnectReturnKind.DESKTOP);
        }

        return Render(http, forms, page, logger);
    }

    private static async Task<IResult> ResumeAsync(
        HttpContext                 http,
        Guid                        handshakeId,
        IClusterClient              cluster,
        IArgonCacheDatabase         cache,
        IOptions<WebSessionOptions> web,
        ClassicJwtFlow              jwt,
        EMailFormStorage            forms,
        ILoggerFactory              loggers,
        CancellationToken           ct)
    {
        if (await IsThrottledAsync(cache, http, ct))
            return Results.StatusCode(StatusCodes.Status429TooManyRequests);

        var browserUserId = BrowserUser(http, web.Value, jwt);
        var logger        = loggers.CreateLogger(typeof(ConnectionsEndpoints));

        HandshakePage page;

        try
        {
            page = await cluster.GetGrain<IConnectionHandshakeGrain>(Guid.Empty).ResumeAsync(handshakeId, browserUserId, ct);
        }
        catch (Exception e)
        {
            logger.LogError(e, "Handshake {HandshakeId} could not be resumed", handshakeId);
            page = new HandshakePage(HandshakePageKind.ProviderError, default, handshakeId, null, null, null, ConnectReturnKind.DESKTOP);
        }

        return Render(http, forms, page, logger);
    }

    /// <summary>Who the browser is, by its web session — or null, which the grain treats as "not signed in".</summary>
    private static Guid? BrowserUser(HttpContext http, WebSessionOptions web, ClassicJwtFlow jwt)
    {
        if (WebAccessCookie.Read(http, web) is not { } token)
            return null;

        try
        {
            var (userId, _, _) = jwt.ValidateAccessToken(token, SessionScope);
            return userId;
        }
        catch
        {
            return null;
        }
    }

    private static Dictionary<string, string> ReadQuery(HttpContext http)
    {
        var query = new Dictionary<string, string>(StringComparer.Ordinal);

        foreach (var (key, values) in http.Request.Query)
        {
            if (query.Count >= MaxQueryValues)
                break;

            var value = values.Count > 0 ? values[0] ?? "" : "";

            if (key.Length > 128 || value.Length > MaxValueLength)
                continue;

            query[key] = value;
        }

        return query;
    }

    /// <summary>Per address; fails open, because a cache outage must not take the callback off the internet.</summary>
    private static async Task<bool> IsThrottledAsync(IArgonCacheDatabase cache, HttpContext http, CancellationToken ct)
    {
        try
        {
            var address = http.Connection.RemoteIpAddress?.ToString() ?? "unknown";
            var window  = DateTimeOffset.UtcNow.ToUnixTimeSeconds() / (long)Window.TotalSeconds;
            var key     = $"conn:cb:{address}:{window}";
            var count   = await cache.StringIncrementAsync(key, ct);

            if (count == 1)
                await cache.UpdateStringExpirationAsync(key, Window, ct);

            return count > RequestsPerWindow;
        }
        catch
        {
            return false;
        }
    }

    private static IResult Render(HttpContext http, EMailFormStorage forms, HandshakePage page, ILogger logger)
    {
        var provider = ConnectionProviders.DisplayName(page.Provider);
        var expected = page.ExpectedUsername is { } expectedName ? "@" + expectedName : "your Argon account";
        var signedIn = page.SignedInUsername is { } signedInName ? "@" + signedInName : "another account";
        var name     = page.ExternalName ?? provider;

        var (title, message) = page.Kind switch
        {
            HandshakePageKind.Linked                 => ("Connected", $"{provider} account {name} is now linked to {expected}. You can go back to Argon."),
            HandshakePageKind.Continue               => ("One more step", "Finishing the connection…"),
            HandshakePageKind.NeedsSignIn            => ("Sign in to finish", $"Sign in to Argon in this browser as {expected}, then continue. The {provider} sign-in is done; nothing is linked until you do."),
            HandshakePageKind.WrongUser              => ("Wrong account", $"This browser is signed in as {signedIn}, but the connection was started from {expected}. Nothing was linked. Sign in as {expected} and start again from Argon."),
            HandshakePageKind.Expired                => ("This link has expired", "Start again from Argon."),
            HandshakePageKind.Denied                 => ("Cancelled", $"The {provider} sign-in was cancelled. Nothing was linked."),
            HandshakePageKind.AlreadyLinkedElsewhere => ("Already connected", $"This {provider} account is connected to another Argon account. Disconnect it there first."),
            HandshakePageKind.ProviderAlreadyLinked  => ("Already connected", $"Your Argon account already has a different {provider} account linked. Disconnect it first, or start again and choose to replace it."),
            HandshakePageKind.ProviderError          => ("Something went wrong", $"{provider} did not answer as expected. Try again in a moment."),
            HandshakePageKind.Disabled               => ("Not available", $"Linking {provider} is not enabled here."),
            HandshakePageKind.Suspended              => ("Not available", $"Your {provider} connection was suspended by moderators and cannot be linked again for now."),
            _                                        => ("Connections", "")
        };

        var continueUrl = page.Kind is HandshakePageKind.Continue or HandshakePageKind.NeedsSignIn
            ? ResumeUrl(page.HandshakeId)
            : "";

        var deepLink = page.Kind == HandshakePageKind.Linked && page.ReturnTo == ConnectReturnKind.DESKTOP
            ? $"argon://connections/linked?provider={ConnectionProviders.Slug(page.Provider)}"
            : "";

        var signInUrl = page.Kind == HandshakePageKind.NeedsSignIn && !string.IsNullOrWhiteSpace(page.SignInUrl)
            ? page.SignInUrl
            : "";

        // Every value is encoded here: the form engine renders raw, and the external name is a
        // string the provider's user typed.
        var values = new Dictionary<string, string>
        {
            ["kind"]          = page.Kind.ToString(),
            ["title"]         = WebUtility.HtmlEncode(title),
            ["message"]       = WebUtility.HtmlEncode(message),
            ["provider"]      = WebUtility.HtmlEncode(provider),
            ["external_name"] = WebUtility.HtmlEncode(name),
            ["continue_url"]  = WebUtility.HtmlEncode(continueUrl),
            ["auto_continue"] = page.Kind == HandshakePageKind.Continue ? "1" : "",
            ["signin_url"]    = WebUtility.HtmlEncode(signInUrl),
            ["deep_link"]     = WebUtility.HtmlEncode(deepLink)
        };

        string html;

        try
        {
            html = forms.Render(PageForm, values);
        }
        catch (InvalidOperationException e)
        {
            logger.LogError(e, "The {Form} page is not loaded; answering plain text", PageForm);
            html = $"<!doctype html><meta name=\"argon-connection-result\" content=\"{values["kind"]}\"><title>{values["title"]}</title>"
                 + $"<h1>{values["title"]}</h1><p>{values["message"]}</p>"
                 + (continueUrl.Length > 0 ? $"<p><a href=\"{values["continue_url"]}\">Continue</a></p>" : "");
        }

        http.Response.Headers.CacheControl   = "no-store";
        http.Response.Headers["Referrer-Policy"] = "no-referrer";
        http.Response.Headers.XFrameOptions  = "DENY";

        return Results.Content(html, "text/html; charset=utf-8");
    }
}
