namespace Argon.Services.Ion;

using Argon.Core.Grains.Interfaces;
using Argon.Features.Integrations.Crawler;
using ion.runtime;

/// <summary>
/// The composer's lookup: asked while the user types, so the card is on screen before the send and
/// the crawler's cache is warm when the message path asks for the same page.
/// </summary>
public sealed class LinkPreviewInteractionImpl(
    ILinkPreviewService previews,
    LinkPreviewRateLimiter limiter,
    IOptions<CrawlerOptions> options) : ILinkPreviewInteraction
{
    public async Task<ILinkPreviewResult> GetLinkPreview(string url, CancellationToken ct = default)
    {
        if (!LinkPreviewUrl.TryNormalize(url, out var normalized))
            return new LinkPreviewFailed(LinkPreviewError.INVALID_URL);

        if (!limiter.TryAcquire(this.GetUserId()))
            return new LinkPreviewFailed(LinkPreviewError.RATE_LIMITED);

        var outcome = await previews.ResolveAsync(normalized, options.Value.Timeout, ct);

        return outcome.Status switch
        {
            LinkPreviewStatus.Ready     => new LinkPreviewReady(outcome.Preview!),
            LinkPreviewStatus.NoPreview => new LinkPreviewFailed(LinkPreviewError.NO_PREVIEW),
            LinkPreviewStatus.Invalid   => new LinkPreviewFailed(LinkPreviewError.INVALID_URL),
            _                           => new LinkPreviewFailed(LinkPreviewError.UNAVAILABLE)
        };
    }
}
