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
    Task                         RemoveSessionAsync(Guid userId, string sessionId, CancellationToken ct = default);
    Task<List<string>>           GetActiveSessionIdsAsync(Guid userId, CancellationToken ct = default);

    // Activity presence is stored PER SESSION (keyed by sid), so different devices of the same user no
    // longer clobber each other's activity. The server already keeps the full per-session set; the
    // current wire still exposes a single ("last") activity via GetUsersActivityPresence, but
    // GetUserActivitiesAsync surfaces the whole set for when the contract grows to multiple activities.
    Task BroadcastActivityPresence(UserActivityPresence presence, Guid userId, string sessionId);

    /// <summary>Every live session's activity for the user (the multi-activity set).</summary>
    Task<List<UserActivityPresence>> GetUserActivitiesAsync(Guid userId);

    Task<Dictionary<Guid, UserActivityPresence>> BatchGetUsersActivityPresence(List<Guid> userIds);

    /// <summary>The single representative ("last") activity for the current single-activity wire.</summary>
    Task<UserActivityPresence?> GetUsersActivityPresence(Guid userId);

    /// <summary>Removes one session's activity. Returns true if that session actually had an activity.</summary>
    Task<bool> RemoveActivityPresence(Guid userId, string sessionId);

    /// <summary>
    /// Sets the preferred status for a specific session and recalculates the aggregated status.
    /// Does NOT refresh the session status TTL - use RefreshSessionStatusTtlAsync for that.
    /// </summary>
    Task SetSessionStatusAsync(Guid userId, string sessionId, UserStatus status, CancellationToken ct = default);

    /// <summary>
    /// Refreshes TTL for session status without recalculating aggregated status.
    /// </summary>
    Task RefreshSessionStatusTtlAsync(Guid userId, string sessionId, CancellationToken ct = default);

    /// <summary>
    /// Removes the status for a specific session and recalculates the aggregated status.
    /// </summary>
    Task RemoveSessionStatusAsync(Guid userId, string sessionId, CancellationToken ct = default);

    /// <summary>
    /// Gets the cached aggregated status for a user. O(1) operation.
    /// </summary>
    Task<UserStatus> GetAggregatedStatusAsync(Guid userId, CancellationToken ct = default);

    /// <summary>
    /// Batch-gets the cached aggregated status for multiple users. O(N) parallel reads.
    /// </summary>
    Task<Dictionary<Guid, UserStatus>> BatchGetAggregatedStatusAsync(List<Guid> userIds, CancellationToken ct = default);

    /// <summary>
    /// Checks whether a specific session's presence key still exists in Redis.
    /// </summary>
    Task<bool> IsSessionAliveAsync(Guid userId, string sessionId, CancellationToken ct = default);

    /// <summary>
    /// Records <paramref name="status"/> as the user's last-broadcast presence and returns true ONLY
    /// if it differs from the previously recorded value (true also when nothing was recorded yet).
    /// Lets the aggregator suppress redundant presence broadcasts — a reconnect/heartbeat that nets
    /// the same aggregate produces no fan-out and no replay-stream write. NOT a substitute for the
    /// multi-session flap fix (the aggregate genuinely changes there); this only kills duplicates.
    /// </summary>
    Task<bool> MarkBroadcastIfChangedAsync(Guid userId, UserStatus status, CancellationToken ct = default);
}

public class UserPresenceService(IArgonCacheDatabase cache) : IUserPresenceService
{
    public static readonly TimeSpan DefaultTTL = TimeSpan.FromSeconds(120);

    private static string SessionKey(Guid userId, string sessionId)
        => $"presence:user:{userId}:session:{sessionId}";

    private static string SessionKeyPrefix(Guid userId)
        => $"presence:user:{userId}:session:*";

    // O(1) index of this user's live session ids (mirrors the TTL'd SessionKey entries).
    private static string SessionsSetKey(Guid userId)
        => $"presence:user:{userId}:sessions";

    // One activity entry per session (sid), so multiple devices don't overwrite each other.
    private static string ActivitySessionKey(Guid userId, string sessionId)
        => $"activity:user:{userId}:session:{sessionId}";

    public Task SetSessionOnlineAsync(Guid userId, string sessionId, CancellationToken ct = default)
        => SetSessionOnlineAsync(userId, sessionId, DefaultTTL, ct);

    public async Task SetSessionOnlineAsync(Guid userId, string sessionId, TimeSpan ttl, CancellationToken ct = default)
    {
        var key = SessionKey(userId, sessionId);
        await cache.StringSetAsync(key, "1", ttl, ct);                  // TTL'd source of truth
        await cache.SetAddAsync(SessionsSetKey(userId), sessionId, ct); // O(1) live-session index
    }

    private Task UpdateSessionAsync(Guid userId, string sessionId, TimeSpan ttl, CancellationToken ct = default)
    {
        var key = SessionKey(userId, sessionId);
        return cache.UpdateStringExpirationAsync(key, ttl, ct);
    }

    public async Task RemoveSessionAsync(Guid userId, string sessionId, CancellationToken ct = default)
    {
        var key = SessionKey(userId, sessionId);
        await cache.KeyDeleteAsync(key, ct);
        await cache.SetRemoveAsync(SessionsSetKey(userId), sessionId, ct);
    }

    public async Task HeartbeatAsync(Guid userId, string sessionId, CancellationToken ct = default)
    {
        await UpdateSessionAsync(userId, sessionId, DefaultTTL, ct);
        // Self-heal the live-session index on every heartbeat: an idempotent SADD re-adds sessions
        // that predate a deploy/cutover (or any lost SADD) so they reappear in presence within one
        // ~15s tick instead of looking offline until reconnect. Covers user and bot sessions, since
        // both route their heartbeat through here.
        await cache.SetAddAsync(SessionsSetKey(userId), sessionId, ct);
    }

    public async Task<bool> IsUserOnlineAsync(Guid userId, CancellationToken ct = default)
    {
        // A session is online only while its TTL'd presence key still exists. Walk the O(1) session
        // index and reconcile against those keys, pruning stale members lazily. No keyspace SCAN.
        foreach (var sessionId in await cache.SetMembersAsync(SessionsSetKey(userId), ct))
        {
            if (await cache.KeyExistsAsync(SessionKey(userId, sessionId), ct))
                return true;
            await cache.SetRemoveAsync(SessionsSetKey(userId), sessionId, ct);
        }

        return false;
    }

    public async Task<Dictionary<Guid, bool>> AreUsersOnlineAsync(IEnumerable<Guid> userIds, CancellationToken ct = default)
    {
        var distinct = userIds.Distinct().ToList();
        var tasks    = distinct.ToDictionary(userId => userId, userId => IsUserOnlineAsync(userId, ct));

        var results = await Task.WhenAll(tasks.Values);

        return tasks.Keys.Zip(results, (key, result) => new { key, result })
           .ToDictionary(x => x.key, x => x.result);
    }

    public async Task<List<string>> GetActiveSessionIdsAsync(Guid userId, CancellationToken ct = default)
    {
        var sessionIds = new List<string>();

        foreach (var sessionId in await cache.SetMembersAsync(SessionsSetKey(userId), ct))
        {
            if (await cache.KeyExistsAsync(SessionKey(userId, sessionId), ct))
                sessionIds.Add(sessionId);
            else
                await cache.SetRemoveAsync(SessionsSetKey(userId), sessionId, ct); // prune stale
        }

        return sessionIds;
    }

    public Task BroadcastActivityPresence(UserActivityPresence presence, Guid userId, string sessionId)
        => cache.StringSetAsync(ActivitySessionKey(userId, sessionId), JsonConvert.SerializeObject(presence), TimeSpan.FromMinutes(10));

    public async Task<List<UserActivityPresence>> GetUserActivitiesAsync(Guid userId)
    {
        // Fold over the user's live sessions (same O(1) index used for status) and read each session's
        // TTL'd activity entry. Expired/empty entries contribute nothing. No keyspace SCAN.
        var activities = new List<UserActivityPresence>();
        foreach (var sessionId in await cache.SetMembersAsync(SessionsSetKey(userId)))
        {
            var json = await cache.StringGetAsync(ActivitySessionKey(userId, sessionId));
            if (string.IsNullOrEmpty(json))
                continue;
            var activity = JsonConvert.DeserializeObject<UserActivityPresence>(json);
            if (activity is not null)
                activities.Add(activity);
        }

        return activities;
    }

    public async Task<Dictionary<Guid, UserActivityPresence>> BatchGetUsersActivityPresence(List<Guid> userIds)
    {
        var distinctIds = userIds.Distinct().ToList();
        var results = await Task.WhenAll(distinctIds.Select(async id => (id, rep: await GetUsersActivityPresence(id))));
        var dict = new Dictionary<Guid, UserActivityPresence>();
        foreach (var (id, rep) in results)
            if (rep is not null)
                dict.TryAdd(id, rep);

        return dict;
    }

    public async Task<UserActivityPresence?> GetUsersActivityPresence(Guid userId)
        => PickRepresentativeActivity(await GetUserActivitiesAsync(userId));

    // The single activity the current wire exposes = the most recently started one across sessions.
    private static UserActivityPresence? PickRepresentativeActivity(List<UserActivityPresence> activities)
        => activities.Count == 0
            ? null
            : activities.OrderByDescending(a => a.startTimestampSeconds).First();

    public async Task<bool> RemoveActivityPresence(Guid userId, string sessionId)
    {
        var key     = ActivitySessionKey(userId, sessionId);
        var existed = !string.IsNullOrEmpty(await cache.StringGetAsync(key));
        if (existed)
            await cache.KeyDeleteAsync(key);
        return existed;
    }

    public async Task SetSessionStatusAsync(Guid userId, string sessionId, UserStatus status, CancellationToken ct = default)
    {
        var key = SessionStatusKey(userId, sessionId);
        await cache.StringSetAsync(key, status.ToString(), DefaultTTL, ct);
        // Ensure the session is in the live-session index so RecalculateAggregatedStatusAsync,
        // which folds over that index, always accounts for this session's status.
        await cache.SetAddAsync(SessionsSetKey(userId), sessionId, ct);
        await RecalculateAggregatedStatusAsync(userId, ct);
    }

    public async Task RefreshSessionStatusTtlAsync(Guid userId, string sessionId, CancellationToken ct = default)
    {
        var key = SessionStatusKey(userId, sessionId);
        await cache.UpdateStringExpirationAsync(key, DefaultTTL, ct);
        await cache.UpdateStringExpirationAsync(AggregatedStatusKey(userId), DefaultTTL, ct);
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

        // Fold over this user's live sessions (O(1) index) and read each session's TTL'd status
        // string key — the source of truth — instead of SCANning the keyspace. A session whose
        // status key has expired returns null and contributes nothing, exactly as the old SCAN
        // (which only ever saw not-yet-expired keys).
        foreach (var sessionId in await cache.SetMembersAsync(SessionsSetKey(userId), ct))
        {
            var statusStr = await cache.StringGetAsync(SessionStatusKey(userId, sessionId), ct);
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

    // The last status we actually broadcast to spaces for this user (presence hysteresis). Kept a bit
    // longer than a session TTL so it bridges the gaps between status-change events; if it does lapse
    // the next change simply re-broadcasts, which is harmless.
    private static string LastBroadcastStatusKey(Guid userId)
        => $"status:user:{userId}:lastbroadcast";

    private static readonly TimeSpan LastBroadcastTTL = TimeSpan.FromMinutes(30);

    public async Task<bool> MarkBroadcastIfChangedAsync(Guid userId, UserStatus status, CancellationToken ct = default)
    {
        var key  = LastBroadcastStatusKey(userId);
        var prev = await cache.StringGetAsync(key, ct);
        if (!string.IsNullOrEmpty(prev) && Enum.TryParse<UserStatus>(prev, out var prevStatus) && prevStatus == status)
            return false;

        await cache.StringSetAsync(key, status.ToString(), LastBroadcastTTL, ct);
        return true;
    }

    public async Task<Dictionary<Guid, UserStatus>> BatchGetAggregatedStatusAsync(List<Guid> userIds, CancellationToken ct = default)
    {
        var distinctIds = userIds.Distinct().ToList();
        var results = await Task.WhenAll(distinctIds.Select(async id =>
        {
            var statusStr = await cache.StringGetAsync(AggregatedStatusKey(id), ct);
            var status = !string.IsNullOrEmpty(statusStr) && Enum.TryParse<UserStatus>(statusStr, out var s)
                ? s
                : UserStatus.Offline;
            return (id, status);
        }));

        return results.ToDictionary(x => x.id, x => x.status);
    }

    public Task<bool> IsSessionAliveAsync(Guid userId, string sessionId, CancellationToken ct = default)
        => cache.KeyExistsAsync(SessionKey(userId, sessionId), ct);
}