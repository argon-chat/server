namespace Argon.Features.Clustering.Regions;

/// <summary>Where a region is, as far as this process can tell.</summary>
public enum RegionStatus
{
    /// <summary>Configured, not connected yet, and not known to be broken.</summary>
    Connecting,

    /// <summary>Connected to at least one gateway. Calls may be routed here.</summary>
    Online,

    /// <summary>Not reachable. Calls must not be routed here, and something is still trying.</summary>
    Offline
}

/// <summary>
/// Keeps the client retrying instead of letting it give up and throw.
/// </summary>
/// <remarks>
/// <para>This is the piece that decides whether an unreachable region is an inconvenience or an
/// outage. <c>OutsideRuntimeClient.StartAsync</c> wraps its three connection steps in a retry loop
/// that consults this filter and <b>rethrows the moment it answers false</b> — so a filter that
/// returns false for anything it does not recognise turns "eu is not answering" into an exception on
/// whatever awaited <c>StartAsync</c>.</para>
///
/// <para>Argon's previous filter retried <c>SiloUnavailableException</c> and returned false for
/// every other exception, and the thing awaiting <c>StartAsync</c> was a subscription callback with
/// no <c>catch</c>. So a region that resolved but refused the connection — a wrong port, a closed
/// gateway, a network policy — took the process down.</para>
///
/// <para>This one retries everything until the token is cancelled. There is no failure of a
/// <em>remote</em> region that this process should treat as fatal: the region is either reachable or
/// it is not, and the registry reports which so callers can route elsewhere. Cancellation is the one
/// way out, and it is what shutdown uses.</para>
/// </remarks>
public sealed class RegionConnectionRetryFilter(string region, TimeSpan maxBackoff, ILogger logger)
    : IClientConnectionRetryFilter
{
    private static readonly TimeSpan BaseDelay = TimeSpan.FromMilliseconds(500);

    /// <summary>How many failures are reported at warning level before it goes quiet.</summary>
    private const int LoudAttempts = 3;

    private int attempts;

    public async Task<bool> ShouldRetryConnectionAttempt(Exception exception, CancellationToken cancellationToken)
    {
        if (cancellationToken.IsCancellationRequested)
            return false;

        var attempt = Interlocked.Increment(ref attempts);
        var delay   = Backoff(attempt, maxBackoff);

        // Loud for the first few, then quiet. Retrying forever is what keeps an unreachable region
        // from being an outage, and it is also what would bury the reason under a million identical
        // lines — while the reason is the only thing anyone wants when a region will not connect.
        if (attempt <= LoudAttempts)
            logger.LogWarning(exception,
                "Region '{Region}' not reachable on attempt {Attempt}; retrying in {Delay}", region, attempt, delay);
        else
            logger.LogDebug(exception,
                "Region '{Region}' not reachable on attempt {Attempt}; retrying in {Delay}", region, attempt, delay);

        try
        {
            await Task.Delay(delay, cancellationToken);
        }
        catch (OperationCanceledException)
        {
            return false;
        }

        return true;
    }

    /// <summary>Resets the backoff, so the next outage does not start where the last one ended.</summary>
    public void Connected() => Interlocked.Exchange(ref attempts, 0);

    /// <summary>Exponential with full jitter, capped.</summary>
    /// <remarks>
    /// Jittered because every process in a region retries the same peer on the same schedule
    /// otherwise, and they all reconnect in the same instant after an outage.
    /// </remarks>
    public static TimeSpan Backoff(int attempt, TimeSpan max)
    {
        var exponent = Math.Min(attempt, 16);
        var scaled   = BaseDelay * Math.Pow(2, exponent - 1);
        var capped   = scaled > max ? max : scaled;

        return TimeSpan.FromMilliseconds(Random.Shared.Next(
            (int)(capped.TotalMilliseconds / 2), (int)capped.TotalMilliseconds + 1));
    }
}

/// <summary>
/// Turns the client's own view of its connection into the registry's view of the region.
/// </summary>
/// <remarks>
/// Constructed outside the client's container and registered into it as an instance, which is what
/// keeps the two containers from needing to see each other. Orleans resolves
/// <c>IClusterConnectionStatusObserver</c> from the client's provider; giving it an object that
/// already closes over the registry means nothing has to be proxied across.
/// </remarks>
public sealed class RegionConnectionObserver(string region, Action<RegionStatus> report, ILogger logger)
    : IClusterConnectionStatusObserver
{
    public void NotifyGatewayCountChanged(int currentNumberOfGateways, int previousNumberOfGateways, bool connectionRecovered)
    {
        // Gateways going to zero is not the same event as the connection being lost, and it arrives
        // first. Treating it as offline is what stops calls being routed into a region during the
        // window between the last gateway leaving and Orleans noticing.
        var status = currentNumberOfGateways > 0 ? RegionStatus.Online : RegionStatus.Offline;

        logger.LogInformation(
            "Region '{Region}' gateways {Previous} -> {Current}{Recovered}, now {Status}",
            region, previousNumberOfGateways, currentNumberOfGateways,
            connectionRecovered ? " (recovered)" : "", status);

        report(status);
    }

    public void NotifyClusterConnectionLost()
    {
        logger.LogWarning("Region '{Region}' connection lost", region);
        report(RegionStatus.Offline);
    }
}
