namespace Argon.Features.Integrations.Crawler;

public interface ICrawlerService
{
    Task<CrawlResult?> CrawlAsync(string url, CancellationToken ct = default);
    Task<CrawlResult?[]> CrawlBatchAsync(string[] urls, CancellationToken ct = default);
    Task<bool> InvalidateAsync(string url, CancellationToken ct = default);
    Task<CrawlHealthResponse?> HealthAsync(CancellationToken ct = default);
}
