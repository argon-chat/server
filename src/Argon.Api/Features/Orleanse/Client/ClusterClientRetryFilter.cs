namespace Argon.Api.Features.Orleans.Client;
using Argon.Features;

public class ClusterClientRetryFilter(ILogger<ClusterClientRetryFilter> logger, [FromKeyedServices("dc")] string dc) : IClientConnectionRetryFilter
{
    private const int BaseDelayMilliseconds = 500;
    private const int MaxDelayMilliseconds = 30_000;
    private int attempt = 0;
    private readonly Random random = new();

    public async Task<bool> ShouldRetryConnectionAttempt(Exception exception, CancellationToken cancellationToken)
    {
        if (exception is SiloUnavailableException)
        {
            attempt++;

            var exponentialDelay = Math.Min(BaseDelayMilliseconds * Math.Pow(2, attempt), MaxDelayMilliseconds);
            var jitter = random.Next((int)(exponentialDelay / 2), (int)exponentialDelay);

            logger.LogDebug("Retry attempt {Attempt} in connection to '{dc}' datacenter, waiting for {Delay}ms before next try",
                attempt, dc, jitter);

            try
            {
                await Task.Delay(jitter, cancellationToken);
            }
            catch (OperationCanceledException)
            {
                return false;
            }

            return true;
        }
        logger.LogWarning(exception, "ShouldRetryConnectionAttempt");
        attempt = 0;
        return false;
    }
}