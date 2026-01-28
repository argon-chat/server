namespace Argon.Api.Features.Orleans.Client;

using System.Security.Cryptography;

public class ClusterClientRetryFilter(ILogger<ClusterClientRetryFilter> logger, [FromKeyedServices("dc")] string dc) : IClientConnectionRetryFilter
{
    private const int BaseDelayMilliseconds = 500;
    private const int MaxDelayMilliseconds = 30_000;
    private int attempt = 0;

    public async Task<bool> ShouldRetryConnectionAttempt(Exception exception, CancellationToken cancellationToken)
    {
        if (exception is SiloUnavailableException)
        {
            attempt++;

            var exponentialDelay = Math.Min(BaseDelayMilliseconds * Math.Pow(2, attempt), MaxDelayMilliseconds);
            // Use RandomNumberGenerator for jitter for consistency with security practices
            // Note: Retry timing is not security-sensitive, but we use cryptographic RNG
            // throughout the codebase to maintain consistent patterns
            var jitter = RandomNumberGenerator.GetInt32((int)(exponentialDelay / 2), (int)exponentialDelay);

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