namespace Argon.Features.Integrations.Crawler;

public interface ICrawlerService
{
    /// <summary>Fetches the page's metadata, from the crawler's cache when it has it. Never throws.</summary>
    Task<CrawlOutcome> CrawlAsync(string url, TimeSpan timeout, CancellationToken ct = default);

    /// <summary>Drops the page from every crawler cache, so the next crawl fetches it again.</summary>
    Task<bool> InvalidateAsync(string url, CancellationToken ct = default);

    Task<CrawlHealthResponse?> HealthAsync(CancellationToken ct = default);
}
