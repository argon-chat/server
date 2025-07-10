namespace Argon.Cassandra.Core;

/// <summary>
/// Provides health check functionality for Cassandra connections
/// </summary>
public class CassandraHealthCheck(ICluster cluster, ILogger? logger = null)
{
    private readonly ICluster _cluster = cluster ?? throw new ArgumentNullException(nameof(cluster));

    /// <summary>
    /// Checks if the Cassandra cluster is healthy and accessible
    /// </summary>
    /// <param name="timeout">Maximum time to wait for health check</param>
    /// <param name="cancellationToken">Cancellation token</param>
    /// <returns>Health check result</returns>
    public async Task<HealthCheckResult> CheckHealthAsync(
        TimeSpan? timeout = null, 
        CancellationToken cancellationToken = default)
    {
        var checkTimeout = timeout ?? TimeSpan.FromSeconds(10);
        var startTime = DateTime.UtcNow;

        try
        {
            using var timeoutCts = new CancellationTokenSource(checkTimeout);
            using var combinedCts = CancellationTokenSource.CreateLinkedTokenSource(
                cancellationToken, timeoutCts.Token);

            // Try to connect and execute a simple query
            using var session = await _cluster.ConnectAsync();
            
            // Execute a simple system query to verify connectivity
            var result = await session.ExecuteAsync(new SimpleStatement("SELECT release_version FROM system.local"));
            
            var version = result.FirstOrDefault()?.GetValue<string>("release_version");
            var duration = DateTime.UtcNow - startTime;

            logger?.LogDebug("Cassandra health check successful. Version: {Version}, Duration: {Duration}ms", 
                version, duration.TotalMilliseconds);

            return new HealthCheckResult
            {
                IsHealthy = true,
                ResponseTime = duration,
                CassandraVersion = version,
                Message = "Cassandra cluster is healthy and responsive"
            };
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            return new HealthCheckResult
            {
                IsHealthy = false,
                ResponseTime = DateTime.UtcNow - startTime,
                Message = "Health check was cancelled"
            };
        }
        catch (TimeoutException)
        {
            var duration = DateTime.UtcNow - startTime;
            logger?.LogWarning("Cassandra health check timed out after {Duration}ms", duration.TotalMilliseconds);
            
            return new HealthCheckResult
            {
                IsHealthy = false,
                ResponseTime = duration,
                Message = $"Health check timed out after {duration.TotalMilliseconds:F0}ms"
            };
        }
        catch (Exception ex)
        {
            var duration = DateTime.UtcNow - startTime;
            logger?.LogError(ex, "Cassandra health check failed after {Duration}ms", duration.TotalMilliseconds);
            
            return new HealthCheckResult
            {
                IsHealthy = false,
                ResponseTime = duration,
                Message = $"Health check failed: {ex.Message}",
                Exception = ex
            };
        }
    }

    /// <summary>
    /// Waits for the Cassandra cluster to become available
    /// </summary>
    /// <param name="maxWaitTime">Maximum time to wait</param>
    /// <param name="retryInterval">Time between retry attempts</param>
    /// <param name="cancellationToken">Cancellation token</param>
    /// <returns>True if cluster becomes available, false if timeout</returns>
    public async Task<bool> WaitForClusterAsync(
        TimeSpan? maxWaitTime = null,
        TimeSpan? retryInterval = null,
        CancellationToken cancellationToken = default)
    {
        var maxWait = maxWaitTime ?? TimeSpan.FromMinutes(5);
        var interval = retryInterval ?? TimeSpan.FromSeconds(5);
        var startTime = DateTime.UtcNow;

        logger?.LogInformation("Waiting for Cassandra cluster to become available (max wait: {MaxWait})", maxWait);

        while (DateTime.UtcNow - startTime < maxWait && !cancellationToken.IsCancellationRequested)
        {
            var healthResult = await CheckHealthAsync(TimeSpan.FromSeconds(5), cancellationToken);
            
            if (healthResult.IsHealthy)
            {
                var totalWaitTime = DateTime.UtcNow - startTime;
                logger?.LogInformation("Cassandra cluster is now available (waited {WaitTime})", totalWaitTime);
                return true;
            }

            logger?.LogDebug("Cassandra cluster not yet available: {Message}. Retrying in {Interval}...", 
                healthResult.Message, interval);

            try
            {
                await Task.Delay(interval, cancellationToken);
            }
            catch (OperationCanceledException)
            {
                break;
            }
        }

        var finalWaitTime = DateTime.UtcNow - startTime;
        logger?.LogWarning("Timed out waiting for Cassandra cluster after {WaitTime}", finalWaitTime);
        return false;
    }
}

public class HealthCheckResult
{
    public bool IsHealthy { get; set; }
    public TimeSpan ResponseTime { get; set; }
    public string? CassandraVersion { get; set; }
    public string Message { get; set; } = string.Empty;
    public Exception? Exception { get; set; }

    public override string ToString()
        => $"Healthy: {IsHealthy}, ResponseTime: {ResponseTime.TotalMilliseconds:F0}ms, Message: {Message}";
}
