namespace Argon.Features.Logic;

using Argon.Core.Features.Logic;
using Argon.Features.Auth;
using Argon.Services.Ion;
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

    /// <summary>
    /// Records who a session belongs to, so it can be named on the devices screen.
    /// </summary>
    /// <remarks>
    /// Presence answers "is this sid alive", which is all the fan-out ever needed; naming a session
    /// needs the client string and the country, and neither is derivable from a sid. Written once
    /// per session from the ion ticket exchange — the one place that already has the whole
    /// <c>ArgonIonTicket</c> in hand — rather than per request, which would put a Redis write on the
    /// hot path to restate a constant.
    /// </remarks>
    Task TouchSessionMetaAsync(Guid userId, string sessionId, string clientName, string region, CancellationToken ct = default);

    /// <summary>
    /// Records a full description of a session the first time it is seen.
    /// </summary>
    /// <remarks>
    /// Returns true when this call created the record and false when one was already there — in
    /// which case nothing is overwritten and only its lifetime is extended. A reconnecting client
    /// asks for a new ticket every time, and the description it gives is the same one; keeping the
    /// first also keeps <see cref="UserSessionMeta.StartedAt"/> honest.
    /// </remarks>
    Task<bool> TouchSessionMetaAsync(Guid userId, string sessionId, UserSessionMeta meta, CancellationToken ct = default);

    /// <summary>The naming record for one session, or null if it was never written or has lapsed.</summary>
    Task<UserSessionMeta?> GetSessionMetaAsync(Guid userId, string sessionId, CancellationToken ct = default);

    /// <summary>Forgets a session's naming record. Paired with <see cref="RemoveSessionAsync"/>.</summary>
    Task RemoveSessionMetaAsync(Guid userId, string sessionId, CancellationToken ct = default);
}

/// <summary>
/// What is known about a session beyond the fact that it is alive.
/// </summary>
/// <remarks>
/// <see cref="LastSeenAt"/> is the last heartbeat, not the last write of this record: the record is
/// written once and the timestamp is refreshed by <c>HeartbeatAsync</c>, which is the only signal
/// that arrives often enough to mean anything. A session whose presence key is alive but whose
/// heartbeat is a minute old is exactly the distinction the devices list is there to show.
/// </remarks>
/// <param name="ClientName">The raw client string — a User-Agent. Kept for the tooltip.</param>
/// <param name="Region">ISO country the session connected from, or "" when the edge said nothing.</param>
/// <param name="AppId">The <c>ner</c> the client carried; resolved to a name through the app registry.</param>
/// <param name="AppName">The name resolved when the record was written, so a session still has one if its id is later dropped from the registry.</param>
public sealed record UserSessionMeta(
    string ClientName,
    string Region,
    DateTime StartedAt,
    DateTime LastSeenAt,
    string? AppId = null,
    string? AppName = null,
    ClientPlatform Platform = ClientPlatform.UNKNOWN,
    string? OsName = null,
    string? AppVersion = null,
    string? DeviceName = null,
    string? Ip = null,
    string? City = null)
{
    /// <summary>Everything a request context knows about the caller, in the shape the devices screen reads.</summary>
    public static UserSessionMeta Describe(ArgonRequestContextData ctx, ClientAppEntry? app)
    {
        var now    = DateTime.UtcNow;
        var client = ctx.Client;

        // The country is stored as "" rather than the "00" sentinel: this record is read by a screen,
        // and the screen's own word for unknown is better than a code that looks like a country.
        var country = ctx.Location.HasCountry
            ? ctx.Location.Country
            : ctx.Region is GeoLocation.UnknownCountry or "" ? "" : ctx.Region;

        return new UserSessionMeta(
            ctx.ClientName,
            country,
            now,
            now,
            AppId: ctx.AppId,
            AppName: ClientIdentity.AppName(app, client),
            Platform: client.Platform,
            OsName: client.OsName,
            AppVersion: client.AppVersion,
            DeviceName: client.DeviceName,
            Ip: ctx.Ip,
            City: ctx.Location.City);
    }
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

    // Who the session is, and when it was last heard from. Split in two because the two halves are
    // written by different things at wildly different rates: the name is a constant established once
    // at session start, the timestamp moves on every ~15s heartbeat. Folding them into one JSON blob
    // would turn each heartbeat into a read-modify-write of a value that never changes.
    private static string SessionMetaKey(Guid userId, string sessionId)
        => $"session:meta:{userId}:{sessionId}";

    private static string SessionSeenKey(Guid userId, string sessionId)
        => $"session:seen:{userId}:{sessionId}";

    // Outlives the 120s presence TTL so a session that briefly drops off and heartbeats back keeps
    // its name instead of reappearing anonymous. Nothing reads it for a session that is not also in
    // the live index, so a stale one is invisible rather than wrong.
    private static readonly TimeSpan SessionMetaTTL = TimeSpan.FromHours(24);

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
        await RemoveSessionMetaAsync(userId, sessionId, ct);
    }

    public Task TouchSessionMetaAsync(Guid userId, string sessionId, string clientName, string region, CancellationToken ct = default)
    {
        var now = DateTime.UtcNow;
        return TouchSessionMetaAsync(userId, sessionId, new UserSessionMeta(clientName, region, now, now), ct);
    }

    public async Task<bool> TouchSessionMetaAsync(Guid userId, string sessionId, UserSessionMeta meta, CancellationToken ct = default)
    {
        var key = SessionMetaKey(userId, sessionId);

        // Two round trips rather than SET NX because the cache abstraction has no NX; the race this
        // leaves is two reconnects of one session writing the same description twice, which is harmless.
        if (await cache.KeyExistsAsync(key, ct))
        {
            await cache.UpdateStringExpirationAsync(key, SessionMetaTTL, ct);
            return false;
        }

        await cache.StringSetAsync(key, JsonConvert.SerializeObject(meta), SessionMetaTTL, ct);
        await cache.StringSetAsync(SessionSeenKey(userId, sessionId), meta.StartedAt.ToString("O"), SessionMetaTTL, ct);

        return true;
    }

    public async Task<UserSessionMeta?> GetSessionMetaAsync(Guid userId, string sessionId, CancellationToken ct = default)
    {
        var json = await cache.StringGetAsync(SessionMetaKey(userId, sessionId), ct);
        var seen = await cache.StringGetAsync(SessionSeenKey(userId, sessionId), ct);

        var lastSeenAt = ParseSeen(seen);

        var meta = string.IsNullOrEmpty(json) ? null : JsonConvert.DeserializeObject<UserSessionMeta>(json);

        // A heartbeat alone is enough to describe a session as "still here, name unknown" — bot
        // sessions never pass through the ticket exchange, and neither does anything that predates
        // the meta record. Returning null for those would drop them off the devices screen entirely,
        // which is the one place a user goes to end a session they do not recognise.
        if (meta is null)
            return lastSeenAt is null ? null : new UserSessionMeta("", "", lastSeenAt.Value, lastSeenAt.Value);

        return lastSeenAt is null ? meta : meta with { LastSeenAt = lastSeenAt.Value };
    }

    /// <summary>
    /// Reads the last-seen stamp in either shape it has been written in.
    /// </summary>
    /// <remarks>
    /// The record used to be stamped with raw ticks while heartbeats wrote round-trip ("O") strings,
    /// and only the ticks were ever parsed — so a session's last-seen froze at the moment it
    /// connected and never moved with its heartbeats. Both shapes are read now; new writes use "O".
    /// </remarks>
    private static DateTime? ParseSeen(string? seen)
    {
        if (string.IsNullOrEmpty(seen))
            return null;

        if (long.TryParse(seen, out var ticks))
            return new DateTime(ticks, DateTimeKind.Utc);

        return DateTime.TryParse(seen, null, System.Globalization.DateTimeStyles.RoundtripKind, out var when)
            ? when.ToUniversalTime()
            : null;
    }

    public async Task RemoveSessionMetaAsync(Guid userId, string sessionId, CancellationToken ct = default)
    {
        await cache.KeyDeleteAsync(SessionMetaKey(userId, sessionId), ct);
        await cache.KeyDeleteAsync(SessionSeenKey(userId, sessionId), ct);
    }

    public async Task HeartbeatAsync(Guid userId, string sessionId, CancellationToken ct = default)
    {
        await UpdateSessionAsync(userId, sessionId, DefaultTTL, ct);
        // Unconditional SET rather than an EXPIRE like the presence key above: this one is allowed to
        // be created by a heartbeat. A session that predates the meta record — or a bot session, which
        // never goes through the ticket exchange — still gets a truthful last-seen, and stays a row
        // the devices screen can offer to end even though it has no name to show.
        await cache.StringSetAsync(SessionSeenKey(userId, sessionId), DateTime.UtcNow.ToString("O"), SessionMetaTTL, ct);
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