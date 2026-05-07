namespace Argon.Features.Integrations.Crawler;

using NATS.Client.Core;

public class NatsCrawlerService(INatsClient nats, IOptions<CrawlerOptions> options, ILogger<NatsCrawlerService> logger) : ICrawlerService
{
    private CrawlerOptions Config => options.Value;

    public async Task<CrawlResult?> CrawlAsync(string url, CancellationToken ct = default)
    {
        var subject = $"{Config.SubjectPrefix}.crawl";
        var request = new CrawlRequest(url);

        using var cts = CancellationTokenSource.CreateLinkedTokenSource(ct);
        cts.CancelAfter(Config.Timeout);

        try
        {
            var reply = await nats.RequestAsync<CrawlRequest, CrawlResult>(subject, request, cancellationToken: cts.Token);
            return reply.Data;
        }
        catch (Exception ex)
        {
            logger.LogWarning(ex, "Crawler request failed for '{Url}'", url);
            return null;
        }
    }

    public async Task<CrawlResult?[]> CrawlBatchAsync(string[] urls, CancellationToken ct = default)
    {
        var subject = $"{Config.SubjectPrefix}.crawl.batch";
        var request = new CrawlBatchRequest(urls);

        using var cts = CancellationTokenSource.CreateLinkedTokenSource(ct);
        cts.CancelAfter(Config.Timeout);

        try
        {
            var reply = await nats.RequestAsync<CrawlBatchRequest, CrawlResult?[]>(subject, request, cancellationToken: cts.Token);
            return reply.Data ?? [];
        }
        catch (Exception ex)
        {
            logger.LogWarning(ex, "Crawler batch request failed for {Count} urls", urls.Length);
            return new CrawlResult?[urls.Length];
        }
    }

    public async Task<bool> InvalidateAsync(string url, CancellationToken ct = default)
    {
        var subject = $"{Config.SubjectPrefix}.invalidate";
        var request = new InvalidateRequest(url);

        using var cts = CancellationTokenSource.CreateLinkedTokenSource(ct);
        cts.CancelAfter(Config.Timeout);

        try
        {
            var reply = await nats.RequestAsync<InvalidateRequest, InvalidateResponse>(subject, request, cancellationToken: cts.Token);
            return reply.Data?.Ok ?? false;
        }
        catch (Exception ex)
        {
            logger.LogWarning(ex, "Crawler invalidation failed for '{Url}'", url);
            return false;
        }
    }

    public async Task<CrawlHealthResponse?> HealthAsync(CancellationToken ct = default)
    {
        var subject = $"{Config.SubjectPrefix}.health";

        using var cts = CancellationTokenSource.CreateLinkedTokenSource(ct);
        cts.CancelAfter(TimeSpan.FromSeconds(5));

        try
        {
            var reply = await nats.RequestAsync<object, CrawlHealthResponse>(subject, new { }, cancellationToken: cts.Token);
            return reply.Data;
        }
        catch (Exception ex)
        {
            logger.LogWarning(ex, "Crawler health check failed");
            return null;
        }
    }
}
