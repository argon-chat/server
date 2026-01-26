namespace Argon.Features.Logic;

using Argon.Core.Features.Logic;
using Services;

public static class UserPresenceFeature
{
    public static IServiceCollection AddUserPresenceFeature(this IHostApplicationBuilder hostBuilder)
    {
        hostBuilder.Services.AddSingleton<IUserPresenceService, UserPresenceService>();
        hostBuilder.Services.AddSingleton<IUserSessionDiscoveryService, LocalUserSessionDiscoveryService>();
        hostBuilder.Services.AddSingleton<IUserSessionNotifier, UserStreamNotifier>();
        hostBuilder.Services.AddHostedService<UserPresenceMetricsService>();
        return hostBuilder.Services;
    }
}

public interface IUserPresenceService
{
    Task                         HeartbeatAsync(Guid userId, string sessionId, CancellationToken ct = default);
    Task<bool>                   IsUserOnlineAsync(Guid userId, CancellationToken ct = default);
    Task<Dictionary<Guid, bool>> AreUsersOnlineAsync(IEnumerable<Guid> userIds, CancellationToken ct = default);
    Task                         SetSessionOnlineAsync(Guid userId, string sessionId, CancellationToken ct = default);
    Task<List<string>>           GetActiveSessionIdsAsync(Guid userId, CancellationToken ct = default);

    Task BroadcastActivityPresence(UserActivityPresence presence, Guid userId, Guid sessionId);

    Task<Dictionary<Guid, UserActivityPresence>> BatchGetUsersActivityPresence(List<Guid> userIds);
    Task<UserActivityPresence?>                  GetUsersActivityPresence(Guid userId);
    Task                                         RemoveActivityPresence(Guid userId);

    /// <summary>
    /// Sets the preferred status for a specific session and recalculates the aggregated status.
    /// </summary>
    Task SetSessionStatusAsync(Guid userId, string sessionId, UserStatus status, CancellationToken ct = default);

    /// <summary>
    /// Removes the status for a specific session and recalculates the aggregated status.
    /// </summary>
    Task RemoveSessionStatusAsync(Guid userId, string sessionId, CancellationToken ct = default);

    /// <summary>
    /// Gets the cached aggregated status for a user. O(1) operation.
    /// </summary>
    Task<UserStatus> GetAggregatedStatusAsync(Guid userId, CancellationToken ct = default);
}

public class UserPresenceService(IArgonCacheDatabase cache) : IUserPresenceService
{
    public static readonly TimeSpan DefaultTTL = TimeSpan.FromSeconds(120);

    private static string SessionKey(Guid userId, string sessionId)
        => $"presence:user:{userId}:session:{sessionId}";

    private static string SessionKeyPrefix(Guid userId)
        => $"presence:user:{userId}:session:*";

    private static string SessionPresenceKey(Guid userId)
        => $"activity:user:{userId}:session:broadcast";

    private static string SessionPresenceKeyPrefix(Guid userId)
        => $"activity:user:{userId}:session:broadcast";

    public Task SetSessionOnlineAsync(Guid userId, string sessionId, CancellationToken ct = default)
        => SetSessionOnlineAsync(userId, sessionId, DefaultTTL, ct);

    public Task SetSessionOnlineAsync(Guid userId, string sessionId, TimeSpan ttl, CancellationToken ct = default)
    {
        var key = SessionKey(userId, sessionId);
        return cache.StringSetAsync(key, "1", ttl, ct);
    }

    private Task UpdateSessionAsync(Guid userId, string sessionId, TimeSpan ttl, CancellationToken ct = default)
    {
        var key = SessionKey(userId, sessionId);
        return cache.UpdateStringExpirationAsync(key, ttl, ct);
    }

    public Task RemoveSessionAsync(Guid userId, string sessionId, CancellationToken ct = default)
    {
        var key = SessionKey(userId, sessionId);
        return cache.KeyDeleteAsync(key, ct);
    }

    public Task HeartbeatAsync(Guid userId, string sessionId, CancellationToken ct = default)
        => UpdateSessionAsync(userId, sessionId, DefaultTTL, ct);

    public async Task<bool> IsUserOnlineAsync(Guid userId, CancellationToken ct = default)
    {
        await foreach (var _ in cache.ScanKeysAsync(SessionKeyPrefix(userId)).WithCancellation(ct))
            return true;

        return false;
    }

    public async Task<Dictionary<Guid, bool>> AreUsersOnlineAsync(IEnumerable<Guid> userIds, CancellationToken ct = default)
    {
        var tasks = userIds.ToDictionary(
            userId => userId,
            userId => Task.Run(async () => {
                await foreach (var _ in cache.ScanKeysAsync(SessionKeyPrefix(userId)).WithCancellation(ct))
                    return true;
                return false;
            }, ct)
        );

        var results = await Task.WhenAll(tasks.Values);

        return tasks.Keys.Zip(results, (key, result) => new { key, result })
           .ToDictionary(x => x.key, x => x.result);
    }

    public async Task<List<string>> GetActiveSessionIdsAsync(Guid userId, CancellationToken ct = default)
    {
        var sessionIds = new List<string>();
        var prefix     = $"presence:user:{userId}:session:";

        await foreach (var key in cache.ScanKeysAsync(SessionKeyPrefix(userId)).WithCancellation(ct))
        {
            if (key.StartsWith(prefix))
                sessionIds.Add(key[prefix.Length..]);
        }

        return sessionIds;
    }

    public async Task BroadcastActivityPresence(UserActivityPresence presence, Guid userId, Guid sessionId)
        => await cache.StringSetAsync(SessionPresenceKey(userId), JsonConvert.SerializeObject(presence), TimeSpan.FromMinutes(10));

    public async Task<Dictionary<Guid, UserActivityPresence>> BatchGetUsersActivityPresence(List<Guid> userIds)
    {
        var keys = await Task.WhenAll(userIds.Select(async x => (await cache.StringGetAsync(SessionPresenceKeyPrefix(x)), x)));
        var dict = new Dictionary<Guid, UserActivityPresence>();
        foreach (var (presence, userId) in keys.Where(x => !string.IsNullOrEmpty(x.Item1)))
            dict.Add(userId, JsonConvert.DeserializeObject<UserActivityPresence>(presence!)!);

        return dict;
    }

    public async Task<UserActivityPresence?> GetUsersActivityPresence(Guid userId)
    {
        var presence = await cache.StringGetAsync(SessionPresenceKeyPrefix(userId));
        return string.IsNullOrEmpty(presence) ? null : JsonConvert.DeserializeObject<UserActivityPresence>(presence);
    }

    public Task RemoveActivityPresence(Guid userId)
        => cache.KeyDeleteAsync(SessionPresenceKeyPrefix(userId));

    public async Task SetSessionStatusAsync(Guid userId, string sessionId, UserStatus status, CancellationToken ct = default)
    {
        var key = SessionStatusKey(userId, sessionId);
        await cache.StringSetAsync(key, status.ToString(), DefaultTTL, ct);
        await RecalculateAggregatedStatusAsync(userId, ct);
    }

    public async Task RemoveSessionStatusAsync(Guid userId, string sessionId, CancellationToken ct = default)
    {
        var key = SessionStatusKey(userId, sessionId);
        await cache.KeyDeleteAsync(key, ct);
        await RecalculateAggregatedStatusAsync(userId, ct);
    }

    /// <summary>
    /// O(1) read of cached aggregated status.
    /// </summary>
    public async Task<UserStatus> GetAggregatedStatusAsync(Guid userId, CancellationToken ct = default)
    {
        var statusStr = await cache.StringGetAsync(AggregatedStatusKey(userId), ct);
        if (string.IsNullOrEmpty(statusStr) || !Enum.TryParse<UserStatus>(statusStr, out var status))
            return UserStatus.Offline;
        return status;
    }

    /// <summary>
    /// Recalculates aggregated status from all sessions and caches it.
    /// Called only when session status changes.
    /// </summary>
    private async Task RecalculateAggregatedStatusAsync(Guid userId, CancellationToken ct = default)
    {
        var aggregatedStatus = UserStatus.Offline;

        await foreach (var key in cache.ScanKeysAsync(SessionStatusKeyPrefix(userId)).WithCancellation(ct))
        {
            var statusStr = await cache.StringGetAsync(key, ct);
            if (string.IsNullOrEmpty(statusStr) || !Enum.TryParse<UserStatus>(statusStr, out var status))
                continue;

            // Priority: DoNotDisturb > Online > Away > Offline
            if (status == UserStatus.DoNotDisturb)
            {
                aggregatedStatus = UserStatus.DoNotDisturb;
                break; // DND always wins, no need to check further
            }

            if (status == UserStatus.Online)
                aggregatedStatus = UserStatus.Online;

            if (status == UserStatus.Away && aggregatedStatus == UserStatus.Offline)
                aggregatedStatus = UserStatus.Away;
        }

        // Cache the aggregated status with same TTL
        await cache.StringSetAsync(AggregatedStatusKey(userId), aggregatedStatus.ToString(), DefaultTTL, ct);
    }

    private static string SessionStatusKey(Guid userId, string sessionId)
        => $"status:user:{userId}:session:{sessionId}";

    private static string SessionStatusKeyPrefix(Guid userId)
        => $"status:user:{userId}:session:*";

    private static string AggregatedStatusKey(Guid userId)
        => $"status:user:{userId}:aggregated";
}