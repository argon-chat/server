namespace Argon.Cassandra.Core;

/// <summary>
/// Provides retry functionality for Cassandra operations with exponential backoff
/// </summary>
public class CassandraRetryPolicy(ILogger? logger = null)
{
    /// <summary>
    /// Executes an operation with retry logic and exponential backoff
    /// </summary>
    /// <typeparam name="T">Return type of the operation</typeparam>
    /// <param name="operation">The operation to execute</param>
    /// <param name="maxRetries">Maximum number of retry attempts</param>
    /// <param name="baseDelay">Base delay between retries</param>
    /// <param name="maxDelay">Maximum delay between retries</param>
    /// <param name="retryMultiplier">Multiplier for exponential backoff</param>
    /// <param name="cancellationToken">Cancellation token</param>
    /// <returns>Result of the operation</returns>
    public async Task<T> ExecuteWithRetryAsync<T>(
        Func<Task<T>> operation,
        int maxRetries = 3,
        TimeSpan? baseDelay = null,
        TimeSpan? maxDelay = null,
        double retryMultiplier = 2.0,
        CancellationToken cancellationToken = default)
    {
        var delay = baseDelay ?? TimeSpan.FromSeconds(1);
        var maxDelayValue = maxDelay ?? TimeSpan.FromSeconds(30);
        var attempt = 0;

        while (true)
        {
            try
            {
                var result = await operation();
                
                if (attempt > 0)
                {
                    logger?.LogInformation("Operation succeeded after {Attempt} retries", attempt);
                }
                
                return result;
            }
            catch (Exception ex) when (attempt < maxRetries && ShouldRetry(ex))
            {
                attempt++;
                var currentDelay = TimeSpan.FromMilliseconds(
                    Math.Min(delay.TotalMilliseconds * Math.Pow(retryMultiplier, attempt - 1), 
                             maxDelayValue.TotalMilliseconds));

                logger?.LogWarning(ex, 
                    "Operation failed (attempt {Attempt}/{MaxRetries}). Retrying in {Delay}ms. Error: {Error}",
                    attempt, maxRetries + 1, currentDelay.TotalMilliseconds, ex.Message);

                try
                {
                    await Task.Delay(currentDelay, cancellationToken);
                }
                catch (OperationCanceledException)
                {
                    throw new OperationCanceledException("Operation was cancelled during retry delay");
                }
            }
            catch (Exception ex)
            {
                if (attempt >= maxRetries)
                {
                    logger?.LogError(ex, "Operation failed after {MaxAttempts} attempts", maxRetries + 1);
                    throw new InvalidOperationException(
                        $"Operation failed after {maxRetries + 1} attempts. Last error: {ex.Message}", ex);
                }
                
                // Non-retryable exception
                logger?.LogError(ex, "Operation failed with non-retryable exception: {Error}", ex.Message);
                throw;
            }
        }
    }

    /// <summary>
    /// Executes an operation with retry logic (void return)
    /// </summary>
    /// <param name="operation">The operation to execute</param>
    /// <param name="maxRetries">Maximum number of retry attempts</param>
    /// <param name="baseDelay">Base delay between retries</param>
    /// <param name="maxDelay">Maximum delay between retries</param>
    /// <param name="retryMultiplier">Multiplier for exponential backoff</param>
    /// <param name="cancellationToken">Cancellation token</param>
    public async Task ExecuteWithRetryAsync(
        Func<Task> operation,
        int maxRetries = 3,
        TimeSpan? baseDelay = null,
        TimeSpan? maxDelay = null,
        double retryMultiplier = 2.0,
        CancellationToken cancellationToken = default)
        => await ExecuteWithRetryAsync<object?>(async () =>
        {
            await operation();
            return null;
        }, maxRetries, baseDelay, maxDelay, retryMultiplier, cancellationToken);

    /// <summary>
    /// Determines if an exception should trigger a retry
    /// </summary>
    /// <param name="exception">The exception to evaluate</param>
    /// <returns>True if the operation should be retried</returns>
    private static bool ShouldRetry(Exception exception)
        => exception switch
        {
            // Network/connection issues - retryable
            TimeoutException           => true,
            OperationCanceledException => false, // Don't retry cancellations
            
            // Cassandra-specific exceptions would go here
            // For now, we'll be conservative and only retry timeouts
            _ when exception.Message.Contains("timeout", StringComparison.OrdinalIgnoreCase)     => true,
            _ when exception.Message.Contains("connection", StringComparison.OrdinalIgnoreCase)  => true,
            _ when exception.Message.Contains("unavailable", StringComparison.OrdinalIgnoreCase) => true,
            
            // Don't retry by default
            _ => false
        };
}

/// <summary>
/// Extension methods for adding retry functionality to DbContext operations
/// </summary>
public static class RetryExtensions
{
    /// <summary>
    /// Adds retry functionality to a CassandraDbContext
    /// </summary>
    /// <param name="context">The context to enhance</param>
    /// <param name="logger">Optional logger for retry operations</param>
    /// <returns>Context with retry capabilities</returns>
    public static CassandraDbContextWithRetry WithRetry(this CassandraDbContext context, ILogger? logger = null)
        => new(context, new CassandraRetryPolicy(logger));
}

/// <summary>
/// Wrapper that adds retry functionality to CassandraDbContext operations
/// </summary>
public class CassandraDbContextWithRetry(CassandraDbContext context, CassandraRetryPolicy retryPolicy) : IDisposable
{
    private readonly CassandraDbContext   _context     = context ?? throw new ArgumentNullException(nameof(context));
    private readonly CassandraRetryPolicy _retryPolicy = retryPolicy ?? throw new ArgumentNullException(nameof(retryPolicy));

    /// <summary>
    /// Saves changes with retry logic
    /// </summary>
    public async Task<int> SaveChangesAsync(
        int maxRetries = 3,
        CancellationToken cancellationToken = default)
        => await _retryPolicy.ExecuteWithRetryAsync(
            () => _context.SaveChangesAsync(cancellationToken),
            maxRetries,
            cancellationToken: cancellationToken);

    /// <summary>
    /// Ensures database schema is created with retry logic
    /// </summary>
    public async Task EnsureCreatedAsync(
        int maxRetries = 3,
        CancellationToken cancellationToken = default)
        => await _retryPolicy.ExecuteWithRetryAsync(
            () => _context.EnsureCreatedAsync(),
            maxRetries,
            cancellationToken: cancellationToken);

    /// <summary>
    /// Access to the underlying context for direct operations
    /// </summary>
    public CassandraDbContext Context => _context;

    public void Dispose()
        => _context?.Dispose();
}
