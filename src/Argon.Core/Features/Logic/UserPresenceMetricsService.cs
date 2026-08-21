namespace Argon.Features.Logic;

using Argon;
using Services;
using System.Diagnostics.Metrics;

/// <summary>
/// Background service that periodically measures online user count from Redis.
/// This ensures accurate metrics even when pods die or sessions are lost.
/// </summary>
public sealed class UserPresenceMetricsService(
    IArgonCacheDatabase cache,
    ILogger<UserPresenceMetricsService> logger)
    : BackgroundService
{
    private static readonly Meter Meter = Instruments.Meter;
    
    private long _lastOnlineCount;

    protected async override Task ExecuteAsync(CancellationToken stoppingToken)
    {
        // Wait a bit before starting to let the service initialize
        await Task.Delay(TimeSpan.FromSeconds(5), CancellationToken.None);

        logger.LogInformation("UserPresenceMetricsService started");

        // Create ObservableGauge after delay to ensure field is accessible
        var onlineCountGauge = Meter.CreateObservableGauge(
            InstrumentNames.UserOnlineCount,
            () => Volatile.Read(ref _lastOnlineCount),
            description: "Current number of online users across all sessions");

        while (!stoppingToken.IsCancellationRequested)
        {
            try
            {
                var count = await CountOnlineUsersAsync(stoppingToken);
                Volatile.Write(ref _lastOnlineCount, count);

                logger.LogDebug("Online users count updated: {OnlineCount}", count);
            }
            catch (Exception ex) when (ex is not OperationCanceledException)
            {
                logger.LogWarning(ex, "Failed to update online users count");
            }

            await Task.Delay(TimeSpan.FromSeconds(15), CancellationToken.None);
        }

        logger.LogInformation("UserPresenceMetricsService stopped");
    }

    private async Task<long> CountOnlineUsersAsync(CancellationToken ct)
    {
        // Scan Redis for unique user IDs that have active sessions
        // Keys are: presence:user:{userId}:session:{sessionId}
        var uniqueUserIds = new HashSet<Guid>();

        await foreach (var key in cache.ScanKeysAsync("presence:user:*:session:*").WithCancellation(ct))
        {
            // Extract userId from key: presence:user:{userId}:session:{sessionId}
            var parts = key.Split(':');
            if (parts.Length >= 3 && Guid.TryParse(parts[2], out var userId))
            {
                uniqueUserIds.Add(userId);
            }
        }

        return uniqueUserIds.Count;
    }
}
