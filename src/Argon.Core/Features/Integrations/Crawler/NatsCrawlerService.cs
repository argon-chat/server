namespace Argon.Features.Integrations.Crawler;

using NATS.Client.Core;

/// <summary>
/// The crawler over NATS request/reply: one JSON request, one JSON reply per subject. The crawler's
/// replicas share a queue group, so whichever is free answers.
/// </summary>
/// <remarks>
/// A crawler that is down is told apart from one that is slow. A request nobody answers — no
/// responders, no connection — fails at once and, after a few in a row, opens a circuit so the
/// message path stops paying a timeout per link. A request that times out is a crawl still running:
/// the crawler keeps going after the requester gives up, and the next ask for the same page finds it
/// in the cache or joins the crawl in flight.
/// </remarks>
public sealed class NatsCrawlerService(
    INatsClient nats,
    IOptions<CrawlerOptions> options,
    ILogger<NatsCrawlerService> logger,
    TimeProvider? time = null) : ICrawlerService
{
    private readonly CrawlerOptions config = options.Value;

    private readonly CrawlerCircuit circuit = new(
        options.Value.CircuitFailureThreshold, options.Value.CircuitOpenFor, time ?? TimeProvider.System);

    public async Task<CrawlOutcome> CrawlAsync(string url, TimeSpan timeout, CancellationToken ct = default)
    {
        if (!config.Enabled)
            return new CrawlOutcome.Unavailable("crawler disabled", TimedOut: false);
        if (circuit.IsOpen)
            return new CrawlOutcome.Unavailable("crawler circuit open", TimedOut: false);

        var subject = $"{config.SubjectPrefix}.crawl";

        using var cts = CancellationTokenSource.CreateLinkedTokenSource(ct);
        cts.CancelAfter(timeout);

        try
        {
            var reply = await nats.RequestAsync<string, string>(subject, CrawlerWire.CrawlRequest(url), cancellationToken: cts.Token);

            var outcome = CrawlerWire.ParseCrawlReply(reply.Data);
            if (outcome is null)
            {
                // Something answered on the crawler's subject with something that is not a reply.
                // Counted like silence: the crawler is not there, whatever is.
                circuit.RecordFailure();
                logger.LogWarning("crawler answered '{Url}' with an unreadable reply", url);
                return new CrawlOutcome.Unavailable("unreadable reply", TimedOut: false);
            }

            circuit.RecordSuccess();
            return outcome;
        }
        catch (OperationCanceledException) when (!ct.IsCancellationRequested)
        {
            // Time, not the caller. The crawler is still on the page; not a failure of the crawler.
            logger.LogInformation("crawler did not answer for '{Url}' within {Timeout}", url, timeout);
            return new CrawlOutcome.Unavailable("timeout", TimedOut: true);
        }
        catch (OperationCanceledException)
        {
            return new CrawlOutcome.Unavailable("cancelled", TimedOut: false);
        }
        catch (NatsNoRespondersException)
        {
            circuit.RecordFailure();
            logger.LogWarning("no crawler is listening on {Subject}", subject);
            return new CrawlOutcome.Unavailable("no responders", TimedOut: false);
        }
        catch (Exception ex)
        {
            circuit.RecordFailure();
            logger.LogWarning(ex, "crawler request failed for '{Url}'", url);
            return new CrawlOutcome.Unavailable(ex.GetType().Name, TimedOut: false);
        }
    }

    public async Task<bool> InvalidateAsync(string url, CancellationToken ct = default)
    {
        if (!config.Enabled) return false;

        var subject = $"{config.SubjectPrefix}.invalidate";

        using var cts = CancellationTokenSource.CreateLinkedTokenSource(ct);
        cts.CancelAfter(config.Timeout);

        try
        {
            var reply = await nats.RequestAsync<string, string>(subject, CrawlerWire.InvalidateRequest(url), cancellationToken: cts.Token);
            return CrawlerWire.ParseInvalidateReply(reply.Data);
        }
        catch (Exception ex)
        {
            logger.LogWarning(ex, "crawler invalidation failed for '{Url}'", url);
            return false;
        }
    }

    public async Task<CrawlHealthResponse?> HealthAsync(CancellationToken ct = default)
    {
        if (!config.Enabled) return null;

        var subject = $"{config.SubjectPrefix}.health";

        using var cts = CancellationTokenSource.CreateLinkedTokenSource(ct);
        cts.CancelAfter(TimeSpan.FromSeconds(5));

        try
        {
            var reply = await nats.RequestAsync<string, string>(subject, string.Empty, cancellationToken: cts.Token);
            return CrawlerWire.ParseHealthReply(reply.Data);
        }
        catch (Exception ex)
        {
            logger.LogWarning(ex, "crawler health check failed");
            return null;
        }
    }
}
