namespace Argon.Features.Logging;

using System.Diagnostics.CodeAnalysis;
using System.Text.RegularExpressions;
using global::Sentry;
using Serilog.Core;
using Serilog.Events;

/// <summary>
/// URLs that carry a credential in the path: an incoming webhook's token and a bot token. They are
/// masked before a path reaches a log, a trace or Sentry.
/// </summary>
public static partial class SecretPaths
{
    public const string Mask = "***";

    private static readonly string[] PathTags = ["url.path", "url.full", "http.route", "http.target", "http.url"];

    // /api/webhooks/{id}/{token}, and /api/bot/{hex}:{secret} as BotPathTokenMiddleware accepts it. A
    // route template ("{token}") is left as it is.
    [GeneratedRegex(@"(?<keep>/api/webhooks/[^/?#\s]+/)[^/?#\s{][^/?#\s]*|(?<keep>/api/bot/)[0-9a-f]{32}(?::|%3a)[^/?#\s]+",
        RegexOptions.IgnoreCase | RegexOptions.CultureInvariant)]
    private static partial Regex Secret();

    [return: NotNullIfNotNull(nameof(value))]
    public static string? Redact(string? value)
    {
        if (string.IsNullOrEmpty(value) || !value.Contains("/api/", StringComparison.OrdinalIgnoreCase))
            return value;

        return Secret().Replace(value, m => m.Groups["keep"].Value + Mask);
    }

    /// <summary>Masks the path tags ASP.NET Core instrumentation puts on a server span.</summary>
    public static void Redact(Activity activity)
    {
        foreach (var tag in PathTags)
        {
            if (activity.GetTagItem(tag) is string value && Redact(value) is var masked && masked != value)
                activity.SetTag(tag, masked);
        }

        activity.DisplayName = Redact(activity.DisplayName);
    }

    public static SentryEvent Redact(SentryEvent evt)
    {
        evt.Request.Url     = Redact(evt.Request.Url);
        evt.TransactionName = Redact(evt.TransactionName);

        if (evt.Message is { } message)
        {
            message.Message   = Redact(message.Message);
            message.Formatted = Redact(message.Formatted);
        }

        return evt;
    }

    /// <summary>The transaction masked, or null when its name carries a secret: a name cannot be changed here.</summary>
    public static SentryTransaction? Redact(SentryTransaction transaction)
    {
        if (Redact(transaction.Name) != transaction.Name)
            return null;

        transaction.Request.Url  = Redact(transaction.Request.Url);
        transaction.Description = Redact(transaction.Description);

        foreach (var span in transaction.Spans)
            span.Description = Redact(span.Description);

        return transaction;
    }

    public static Breadcrumb Redact(Breadcrumb crumb)
    {
        var message = Redact(crumb.Message);
        var data    = crumb.Data?.ToDictionary(kv => kv.Key, kv => Redact(kv.Value));

        if (message == crumb.Message && (crumb.Data is null || crumb.Data.All(kv => data![kv.Key] == kv.Value)))
            return crumb;

        return new Breadcrumb(message ?? "", crumb.Type ?? "default", data, crumb.Category, crumb.Level);
    }
}

/// <summary>Masks <c>RequestPath</c>, which ASP.NET Core's request scope puts on every log line of a request.</summary>
public sealed class SecretPathEnricher : ILogEventEnricher
{
    public void Enrich(LogEvent logEvent, ILogEventPropertyFactory propertyFactory)
    {
        if (logEvent.Properties.TryGetValue("RequestPath", out var value) && value is ScalarValue { Value: string path }
         && SecretPaths.Redact(path) is var masked && masked != path)
            logEvent.AddOrUpdateProperty(new LogEventProperty("RequestPath", new ScalarValue(masked)));
    }
}
