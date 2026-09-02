namespace Argon.Features.Integrations.Crawler;

using System.Text.Json;
using System.Text.Json.Serialization;

/// <summary>
/// The wire shapes of argon-crawler 1.4 (its README, "NATS API"): JSON both ways, camelCase, and a
/// reply that is either a result or an error told apart by the presence of <c>error</c>. The crawler
/// never answers with a NATS error, so a missing reply means nobody was there to send one.
/// </summary>
public static class CrawlerWire
{
    public static readonly JsonSerializerOptions Json = new(JsonSerializerDefaults.Web)
    {
        DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull,
        NumberHandling         = JsonNumberHandling.AllowReadingFromString,
    };

    public static string CrawlRequest(string url)
        => JsonSerializer.Serialize(new CrawlRequest(url), Json);

    public static string InvalidateRequest(string url)
        => JsonSerializer.Serialize(new InvalidateRequest(url), Json);

    /// <summary>
    /// Reads one <c>crawl</c> reply. Null when the payload is not a reply at all — not JSON, or JSON
    /// that names neither a URL nor an error.
    /// </summary>
    public static CrawlOutcome? ParseCrawlReply(string? payload)
    {
        if (string.IsNullOrWhiteSpace(payload))
            return null;

        CrawlReplyJson? reply;
        try
        {
            reply = JsonSerializer.Deserialize<CrawlReplyJson>(payload, Json);
        }
        catch (JsonException)
        {
            return null;
        }

        if (reply is null)
            return null;

        if (reply.Error is not null)
            return new CrawlOutcome.Failure(new CrawlError(
                reply.Url ?? string.Empty, reply.Error, reply.Code ?? "INTERNAL_ERROR",
                reply.StatusCode, reply.FromCache ?? false));

        if (reply.Url is null)
            return null;

        return new CrawlOutcome.Success(new CrawlResult(
            reply.Url, reply.Title, reply.Description, reply.Image, reply.ImageStored,
            reply.SiteName, reply.Type, reply.Favicon, reply.Canonical, reply.FromCache ?? false));
    }

    public static bool ParseInvalidateReply(string? payload)
    {
        if (string.IsNullOrWhiteSpace(payload)) return false;
        try
        {
            return JsonSerializer.Deserialize<InvalidateResponse>(payload, Json)?.Ok ?? false;
        }
        catch (JsonException)
        {
            return false;
        }
    }

    public static CrawlHealthResponse? ParseHealthReply(string? payload)
    {
        if (string.IsNullOrWhiteSpace(payload)) return null;
        try
        {
            return JsonSerializer.Deserialize<CrawlHealthResponse>(payload, Json);
        }
        catch (JsonException)
        {
            return null;
        }
    }

    /// <summary>Every field optional, so a result and an error read through the same type.</summary>
    private sealed class CrawlReplyJson
    {
        public string? Url         { get; init; }
        public string? Title       { get; init; }
        public string? Description { get; init; }
        public string? Image       { get; init; }
        public string? ImageStored { get; init; }
        public string? SiteName    { get; init; }
        public string? Type        { get; init; }
        public string? Favicon     { get; init; }
        public string? Canonical   { get; init; }
        public bool?   FromCache   { get; init; }
        public string? Error       { get; init; }
        public string? Code        { get; init; }
        public int?    StatusCode  { get; init; }
    }
}

public sealed record CrawlRequest(string Url);

public sealed record InvalidateRequest(string Url);

public sealed record InvalidateResponse(bool Ok);

public sealed record CrawlHealthResponse(string Status, string? Version, long Uptime);

/// <summary>What the crawler found on a page. <c>ImageStored</c> is its own re-hosted copy of the OG image.</summary>
public sealed record CrawlResult(
    string  Url,
    string? Title,
    string? Description,
    string? Image,
    string? ImageStored,
    string? SiteName,
    string? Type,
    string? Favicon,
    string? Canonical,
    bool    FromCache);

/// <summary>The crawler's refusal: <c>Code</c> is one of its documented codes (ROBOTS_BLOCKED, HTTP_ERROR, ...).</summary>
public sealed record CrawlError(string Url, string Error, string Code, int? StatusCode, bool FromCache);

/// <summary>
/// How a crawl ended. A <see cref="Failure"/> is the crawler's answer and final for that page; an
/// <see cref="Unavailable"/> is the transport's — nothing was learnt about the page.
/// </summary>
public abstract record CrawlOutcome
{
    private CrawlOutcome() { }

    public sealed record Success(CrawlResult Result) : CrawlOutcome;

    public sealed record Failure(CrawlError Error) : CrawlOutcome;

    /// <param name="TimedOut">
    /// True when the request ran out of time — the crawl may still be running and a later ask can
    /// find it in the cache. False when nobody answered at all: no responders, no connection, an
    /// open circuit, or a reply that was not one.
    /// </param>
    public sealed record Unavailable(string Reason, bool TimedOut) : CrawlOutcome;
}
