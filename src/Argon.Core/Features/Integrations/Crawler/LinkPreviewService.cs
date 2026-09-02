namespace Argon.Features.Integrations.Crawler;

using System.Collections.Concurrent;
using System.Diagnostics.CodeAnalysis;

public enum LinkPreviewStatus
{
    Ready,

    /// <summary>The page was looked at and has nothing worth a card, or the crawler was refused it. Final.</summary>
    NoPreview,

    /// <summary>Not an absolute http(s) URL. Final.</summary>
    Invalid,

    /// <summary>Nothing was learnt about the page; <see cref="LinkPreviewOutcome.Retryable"/> says whether asking again can help.</summary>
    Unavailable
}

/// <param name="Retryable">
/// For <see cref="LinkPreviewStatus.Unavailable"/>: the crawler was on the page when time ran out,
/// so a later ask with more patience can find it in the cache. False when nothing is listening.
/// </param>
public readonly record struct LinkPreviewOutcome(LinkPreviewStatus Status, LinkPreview? Preview = null, bool Retryable = false)
{
    public static LinkPreviewOutcome Ready(LinkPreview preview) => new(LinkPreviewStatus.Ready, preview);

    public static readonly LinkPreviewOutcome NoPreview = new(LinkPreviewStatus.NoPreview);
    public static readonly LinkPreviewOutcome Invalid   = new(LinkPreviewStatus.Invalid);

    public static LinkPreviewOutcome Unavailable(bool retryable) => new(LinkPreviewStatus.Unavailable, Retryable: retryable);
}

public interface ILinkPreviewService
{
    /// <summary>Turns a link into the card clients show for it. Never throws.</summary>
    Task<LinkPreviewOutcome> ResolveAsync(string url, TimeSpan timeout, CancellationToken ct = default);
}

/// <summary>
/// What the message path and the composer lookup share: URL hygiene, the crawler, and the mapping of
/// what it found into a <see cref="LinkPreview"/>.
/// </summary>
public sealed class LinkPreviewService(ICrawlerService crawler, IOptions<CrawlerOptions> options) : ILinkPreviewService
{
    public async Task<LinkPreviewOutcome> ResolveAsync(string url, TimeSpan timeout, CancellationToken ct = default)
    {
        if (!LinkPreviewUrl.TryNormalize(url, out var normalized))
            return LinkPreviewOutcome.Invalid;

        if (!options.Value.Enabled)
            return LinkPreviewOutcome.Unavailable(retryable: false);

        var outcome = await crawler.CrawlAsync(normalized, timeout, ct);

        return outcome switch
        {
            CrawlOutcome.Success s => LinkPreviewMapper.ToPreview(s.Result, normalized, options.Value.AllowExternalImages) is { } preview
                ? LinkPreviewOutcome.Ready(preview)
                : LinkPreviewOutcome.NoPreview,
            CrawlOutcome.Failure f => f.Error.Code is "INVALID_URL" or "UNSUPPORTED_PROTOCOL"
                ? LinkPreviewOutcome.Invalid
                : LinkPreviewOutcome.NoPreview,
            CrawlOutcome.Unavailable u => LinkPreviewOutcome.Unavailable(u.TimedOut),
            _                          => LinkPreviewOutcome.Unavailable(retryable: false)
        };
    }
}

public static class LinkPreviewMapper
{
    // The crawler already caps at 512/2048; these are what a card can show. A message row carries
    // the card in its entities column, so the cap is also what every reader downloads.
    public const int MaxTitle       = 256;
    public const int MaxDescription = 512;
    public const int MaxSiteName    = 128;

    /// <summary>Null when the page carries nothing a card could show.</summary>
    public static LinkPreview? ToPreview(CrawlResult result, string url, bool allowExternalImages)
    {
        var title       = Clip(result.Title, MaxTitle);
        var description = Clip(result.Description, MaxDescription);

        // The re-hosted copy or nothing, unless a deployment says otherwise: the original is a
        // request from every reader to the linked site.
        var image = LinkPreviewUrl.HttpOrNull(result.ImageStored)
                    ?? (allowExternalImages ? LinkPreviewUrl.HttpOrNull(result.Image) : null);

        if (title is null && description is null && image is null)
            return null;

        var siteName  = Clip(result.SiteName, MaxSiteName) ?? LinkPreviewUrl.HostOf(url);
        var canonical = LinkPreviewUrl.HttpOrNull(result.Canonical);
        if (string.Equals(canonical, url, StringComparison.Ordinal))
            canonical = null;

        return new LinkPreview(url, title, description, siteName, image, canonical);
    }

    private static string? Clip(string? value, int max)
    {
        if (string.IsNullOrWhiteSpace(value)) return null;
        var text = value.Trim();
        return text.Length <= max ? text : string.Concat(text.AsSpan(0, max - 1).TrimEnd(), "…");
    }
}

public static class LinkPreviewUrl
{
    public const int MaxLength = 2048;

    /// <summary>
    /// Accepts an absolute http(s) URL with a host and no credentials, and hands back the form the
    /// crawler is asked for and the card links to: scheme, host, path and query, no fragment.
    /// </summary>
    public static bool TryNormalize(string? raw, [NotNullWhen(true)] out string? normalized)
    {
        normalized = null;
        if (string.IsNullOrWhiteSpace(raw) || raw.Length > MaxLength)
            return false;
        if (!Uri.TryCreate(raw.Trim(), UriKind.Absolute, out var uri))
            return false;
        if (uri.Scheme is not ("http" or "https"))
            return false;
        if (string.IsNullOrEmpty(uri.Host) || uri.HostNameType == UriHostNameType.Unknown)
            return false;
        // "user:secret@host" is a phishing shape, never a page anyone previews.
        if (!string.IsNullOrEmpty(uri.UserInfo))
            return false;

        normalized = uri.GetComponents(UriComponents.SchemeAndServer | UriComponents.PathAndQuery, UriFormat.UriEscaped);
        return normalized.Length <= MaxLength;
    }

    /// <summary>The URL itself when it is http(s); otherwise null. For values the crawler passes through from a page.</summary>
    public static string? HttpOrNull(string? raw)
        => TryNormalize(raw, out var normalized) ? normalized : null;

    public static string HostOf(string url)
        => Uri.TryCreate(url, UriKind.Absolute, out var uri) ? uri.Host : url;

    /// <summary>
    /// Whether the text visibly carries the link, scheme aside, so a card can never point somewhere
    /// the message does not. Tolerant of the two ways a client writes the same address: with or
    /// without a scheme, with or without a trailing slash.
    /// </summary>
    public static bool AppearsIn(string? text, string url)
    {
        if (string.IsNullOrEmpty(text) || string.IsNullOrEmpty(url))
            return false;

        var needle = StripScheme(url).TrimEnd('/');
        if (needle.Length == 0)
            return false;

        if (text.Contains(needle, StringComparison.OrdinalIgnoreCase))
            return true;

        // The stored form escapes what the user typed unescaped (spaces, unicode paths).
        var unescaped = Uri.UnescapeDataString(needle);
        return unescaped != needle && text.Contains(unescaped, StringComparison.OrdinalIgnoreCase);
    }

    private static string StripScheme(string url)
    {
        var at = url.IndexOf("://", StringComparison.Ordinal);
        return at < 0 ? url : url[(at + 3)..];
    }
}

/// <summary>
/// What a client may say about a link preview, and what the server fills in. The client attaches a
/// stub — the URL and where it sits in the text — and nothing else survives: title, description,
/// image and canonical are the crawler's, because a card the sender could write is a phishing kit.
/// </summary>
public static class LinkPreviewEntities
{
    /// <summary>
    /// Keeps at most one stub from what the client sent — the first whose URL is valid and visibly in
    /// the text — cleared down to the URL, and removes every other link-preview entity from the list.
    /// </summary>
    public static MessageEntityLinkPreview? TakeStub(List<IMessageEntity> entities, string text)
    {
        MessageEntityLinkPreview? kept = null;

        for (var i = 0; i < entities.Count; i++)
        {
            if (entities[i] is not MessageEntityLinkPreview candidate)
                continue;

            if (kept is null
                && LinkPreviewUrl.TryNormalize(candidate.url, out var url)
                && (LinkPreviewUrl.AppearsIn(text, candidate.url) || LinkPreviewUrl.AppearsIn(text, url)))
            {
                var (offset, length) = ClampSpan(candidate.offset, candidate.length, text.Length);
                kept = new MessageEntityLinkPreview(EntityType.LinkPreview, offset, length, candidate.version,
                    url, null, null, null, null, null);
                entities[i] = kept;
                continue;
            }

            entities.RemoveAt(i--);
        }

        return kept;
    }

    public static MessageEntityLinkPreview Fill(MessageEntityLinkPreview stub, LinkPreview preview)
        => stub with
        {
            url          = preview.url,
            title        = preview.title,
            description  = preview.description,
            siteName     = preview.siteName,
            imageUrl     = preview.imageUrl,
            canonicalUrl = preview.canonicalUrl
        };

    /// <summary>A stub the crawler has not answered for yet: nothing a card could show.</summary>
    public static bool IsPending(MessageEntityLinkPreview entity)
        => entity.title is null && entity.description is null && entity.imageUrl is null;

    /// <summary>Where the link sits in the text; a span that does not fit becomes "nowhere" rather than being refused.</summary>
    private static (int Offset, int Length) ClampSpan(int offset, int length, int textLength)
    {
        if (offset < 0 || length < 0 || offset > textLength || offset + length > textLength)
            return (0, 0);
        return (offset, length);
    }
}

/// <summary>
/// Composer lookups per user, a fixed window per minute. Per node on purpose: this stops one client
/// from turning the crawler into a service for itself, not a coordinated quota.
/// </summary>
public sealed class LinkPreviewRateLimiter(IOptions<CrawlerOptions> options, TimeProvider? time = null)
{
    private const int PruneAbove = 10_000;

    private readonly ConcurrentDictionary<Guid, Window> windows = new();
    private readonly TimeProvider                        clock   = time ?? TimeProvider.System;

    public bool TryAcquire(Guid userId)
    {
        var limit  = options.Value.PreviewRequestsPerMinute;
        var minute = clock.GetUtcNow().ToUnixTimeSeconds() / 60;

        var window = windows.AddOrUpdate(userId,
            _ => new Window(minute, 1),
            (_, w) => w.Minute == minute ? w with { Count = w.Count + 1 } : new Window(minute, 1));

        if (windows.Count > PruneAbove)
        {
            foreach (var (key, value) in windows)
            {
                if (value.Minute != minute)
                    windows.TryRemove(key, out _);
            }
        }

        return window.Count <= limit;
    }

    private readonly record struct Window(long Minute, int Count);
}
