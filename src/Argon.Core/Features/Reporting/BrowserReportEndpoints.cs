namespace Argon.Features.Reporting;

using System.Text.Json;
using Argon.Features.WebSession;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Routing;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;

/// <summary>
/// Where the browser delivers the reports nothing else can tell us about.
/// </summary>
/// <remarks>
/// <para><b>What this is for, and it is one thing above all: crashes.</b> When a tab is killed — out
/// of memory, a renderer fault — the JavaScript in it dies at the same instant, and with it every
/// error handler that would have said so. Sentry's browser SDK cannot report the death of the thing
/// it lives inside. A Reporting API report is delivered by the browser itself, after the fact and
/// out of band, which is the only way that event ever reaches us. <c>deprecation</c> and
/// <c>intervention</c> arrive the same way and are worth having, but they are not why this exists.
/// </para>
///
/// <para><b>Why the browser is not pointed straight at Sentry.</b> Sentry documents its
/// <c>security/</c> endpoint for <c>Reporting-Endpoints</c>, but documents it for CSP: that endpoint
/// reads a violation, and it answers 200 to anything at all, so a crash report posted there is
/// accepted and then dropped with nothing to show for it. CSP reports do still go straight there —
/// they group properly and need nothing from us. Everything else comes here and becomes an ordinary
/// Sentry event, so it lands as an issue rather than as a line in a log.</para>
///
/// <para><b>This endpoint is unauthenticated and world-writable, because it has to be.</b> A browser
/// reporting a crash has no session to present, and the tab that held one is gone. So it is built to
/// be a poor target: the body is capped as it is read, a handful of reports are taken from each one,
/// every string is truncated, unknown types are dropped, and the <c>Origin</c> must be a web client
/// this deployment already trusts. That last check is forgeable by a script and not by a browser,
/// which is the difference between casual abuse of somebody's Sentry quota and a deliberate one.
/// </para>
/// </remarks>
public static class BrowserReportEndpoints
{
    public const string Path = "/telemetry/reports";

    /// <summary>
    /// Its own CORS policy: reports are POSTed cross-origin and preflighted, while the shared public
    /// policy allows GET alone.
    /// </summary>
    public const string CorsPolicy = "BrowserReports";

    /// <summary>The Reporting API's own content type. Anything else did not come from a browser.</summary>
    private const string ContentType = "application/reports+json";

    /// <summary>Generous for a real batch, small enough that this is not a pipe.</summary>
    private const int MaxBodyBytes = 64 * 1024;

    /// <summary>A browser batches a handful. A flood is somebody else.</summary>
    private const int MaxReports = 20;

    /// <summary>Long enough to identify a page, short enough that nothing here is a payload.</summary>
    private const int MaxFieldLength = 512;

    /// <summary>
    /// The types worth an issue. Anything else — including a type a caller invented — is dropped.
    /// </summary>
    /// <remarks>
    /// <c>csp-violation</c> is absent deliberately: those go to Sentry's own endpoint, which
    /// understands them. Taking them here as well would file every violation twice.
    /// </remarks>
    internal static readonly HashSet<string> Accepted =
        new(StringComparer.OrdinalIgnoreCase) { "crash", "deprecation", "intervention" };

    public static WebApplicationBuilder AddBrowserReports(this WebApplicationBuilder builder)
    {
        builder.Services.AddCors(o => o.AddPolicy(CorsPolicy, p =>
            p.AllowAnyOrigin()
             .AllowAnyHeader()
             .WithMethods("POST", "OPTIONS")));

        return builder;
    }

    public static WebApplication MapBrowserReports(this WebApplication app)
    {
        app.MapPost(Path, HandleAsync).AllowAnonymous().RequireCors(CorsPolicy);

        return app;
    }

    /// <remarks>
    /// Always 204, whatever happened. A sender learns nothing from the answer — not whether its
    /// origin was accepted, not whether its body parsed — and the browser does not act on it either:
    /// the Reporting API retries on its own schedule, so a refusal would only buy another attempt at
    /// something that will fail the same way.
    /// </remarks>
    private static async Task<IResult> HandleAsync(
        HttpContext                 http,
        IOptions<WebSessionOptions> webSession,
        ILoggerFactory              loggers,
        CancellationToken           ct)
    {
        var log = loggers.CreateLogger(typeof(BrowserReportEndpoints));

        if (!IsOurOrigin(http, webSession.Value) || !IsReportBody(http))
            return Results.NoContent();

        // Read against the cap rather than trusting Content-Length, which is the caller's claim.
        var buffer = new byte[MaxBodyBytes];
        var read   = await ReadCappedAsync(http.Request.Body, buffer, ct);

        // Empty, or bigger than any batch a browser sends — in which case it was truncated and there
        // is no honest way to read what is left.
        if (read is 0 or MaxBodyBytes)
            return Results.NoContent();

        try
        {
            using var document = JsonDocument.Parse(buffer.AsMemory(0, read));

            if (document.RootElement.ValueKind != JsonValueKind.Array)
                return Results.NoContent();

            var taken = 0;

            foreach (var report in document.RootElement.EnumerateArray())
            {
                if (taken++ >= MaxReports)
                    break;

                Record(report, log);
            }
        }
        catch (JsonException)
        {
            // Malformed. Nothing to tell the sender, and nothing worth logging: anyone can reach
            // this endpoint, so bad JSON here is noise rather than news.
        }

        return Results.NoContent();
    }

    private static void Record(JsonElement report, ILogger log)
    {
        if (Text(report, "type") is not { } type || !Accepted.Contains(type))
            return;

        var url  = Text(report, "url") ?? "unknown";
        var body = report.TryGetProperty("body", out var carried) ? carried : default;

        // The one field that says what happened, by type. It belongs in the message so that issues
        // group by cause, rather than collapsing into a single "browser report" with thousands of
        // events under it.
        var reason = type.ToLowerInvariant() switch
        {
            "crash" => Text(body, "reason") ?? "unknown",
            _       => Text(body, "id") ?? "unknown",
        };

        var evt = new SentryEvent
        {
            Logger = "browser-report",
            Level = type.Equals("crash", StringComparison.OrdinalIgnoreCase)
                ? SentryLevel.Error
                : SentryLevel.Warning,
            Message = new SentryMessage
            {
                Message   = $"browser {type} report: {reason}",
                Formatted = $"browser {type} report: {reason}",
            },
        };

        evt.SetTag("report.type", type);
        evt.SetTag("report.reason", reason);
        evt.SetExtra("url", url);
        evt.SetExtra("user_agent", Text(report, "user_agent") ?? "unknown");

        if (body.ValueKind == JsonValueKind.Object)
            evt.SetExtra("body", Truncate(body.GetRawText()));

        // A no-op when no DSN is configured, which is how a local run and the test host stay quiet.
        SentrySdk.CaptureEvent(evt);

        log.LogInformation("Browser {ReportType} report from {Url}: {Reason}", type, url, reason);
    }

    /// <summary>
    /// Whether this came from a web client this deployment serves.
    /// </summary>
    /// <remarks>
    /// The same allowlist the session exchange checks an audience against, rather than a second one
    /// to keep in step with it. A self-hosted instance therefore takes reports from its own client
    /// and nobody else's, without being configured to.
    /// </remarks>
    internal static bool IsOurOrigin(HttpContext http, WebSessionOptions options)
        => http.Request.Headers.TryGetValue("Origin", out var origin)
        && origin.ToString() is { Length: > 0 } value
        && options.TrustedAudiences.Contains(value, StringComparer.OrdinalIgnoreCase);

    internal static bool IsReportBody(HttpContext http)
        => http.Request.ContentType?.StartsWith(ContentType, StringComparison.OrdinalIgnoreCase) == true;

    /// <summary>Reads at most <paramref name="buffer"/>, so an endless body cannot fill memory.</summary>
    internal static async Task<int> ReadCappedAsync(Stream body, byte[] buffer, CancellationToken ct)
    {
        var total = 0;

        while (total < buffer.Length)
        {
            var read = await body.ReadAsync(buffer.AsMemory(total), ct);

            if (read == 0)
                break;

            total += read;
        }

        return total;
    }

    private static string? Text(JsonElement element, string property)
        => element.ValueKind == JsonValueKind.Object
        && element.TryGetProperty(property, out var value)
        && value.ValueKind == JsonValueKind.String
            ? Truncate(value.GetString())
            : null;

    internal static string? Truncate(string? value)
        => value is { Length: > MaxFieldLength } ? value[..MaxFieldLength] : value;
}
