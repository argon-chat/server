namespace Argon.Features.Integrations.Crawler;

public static class CrawlerFeature
{
    /// <summary>
    /// The crawler client and what sits on it: the preview resolver the message path and the
    /// composer share, and the per-user limiter the composer lookup goes through. Options are bound
    /// by the feature declaration that calls this.
    /// </summary>
    public static IServiceCollection AddCrawlerFeature(this WebApplicationBuilder builder)
    {
        builder.Services.AddSingleton<ICrawlerService, NatsCrawlerService>();
        builder.Services.AddSingleton<ILinkPreviewService, LinkPreviewService>();
        builder.Services.AddSingleton<LinkPreviewRateLimiter>();
        return builder.Services;
    }
}
