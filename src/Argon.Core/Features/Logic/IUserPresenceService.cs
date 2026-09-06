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

/// <summary>
/// What one tick found of a session's activity lease, and therefore what the ticking grain owes the
/// rooms the user is in.
/// </summary>
/// <remarks>
/// Three answers rather than a bool because the two "nothing to renew" cases are not the same thing:
/// a session that never announced anything owes the room nothing, while a session whose entry or
/// whose lease has ended owes it a retraction. Collapsing them is how an activity came to survive its
/// own key — see <see cref="IUserPresenceService.RefreshSessionStatusTtlAsync"/>.
/// </remarks>
public enum ActivityLeaseState
{
    /// <summary>This session has announced no activity, so there is nothing to renew or to retract.</summary>
    Absent,

    /// <summary>The entry is there and its lease is inside the window; both were pushed back to full.</summary>
    Renewed,

    /// <summary>
    /// Over. The entry is gone or the lease has run out, and the caller owes the room one
    /// <c>OnUserPresenceActivityRemoved</c> (or, if another device is still playing something, that
    /// device's activity instead).
    /// </summary>
    Expired
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
    /// Records the status one session reports, and nothing else.
    /// </summary>
    /// <remarks>
    /// <para>Does NOT refresh the session status TTL — use <see cref="RefreshSessionStatusTtlAsync"/>
    /// for that — and, since the presence grain landed, does NOT recompute
    /// <c>status:user:{u}:aggregated</c> either. <b>The caller owes the user's
    /// <c>IUserPresenceGrain</c> a re-aggregation.</b></para>
    ///
    /// <para>It used to fold here, and folding here is what made the fold concurrent: this is called
    /// from a session grain, a user has several of them, and two sessions moving at once produced two
    /// overlapping read-fold-writes of one key. The atomic Lua fold that fixed it has had to go (it
    /// reads keys it does not declare, which Dragonfly refuses by default and, when allowed, runs
    /// under a global lock), so the mutual exclusion is Orleans' instead: one activation per user,
    /// turn by turn, in <c>UserPresenceGrain</c>. That only holds while every fold goes through it —
    /// hence a writer that writes and stops.</para>
    /// </remarks>
    Task SetSessionStatusAsync(Guid userId, string sessionId, UserStatus status, CancellationToken ct = default);

    /// <summary>
    /// Refreshes the TTLs a session owns — its status, the user's aggregate, and its activity —
    /// without recalculating the aggregated status.
    /// </summary>
    /// <remarks>
    /// <para>This is the session keep-alive: the session grain's 15 s tick calls it, and so does the
    /// bot gateway's. Everything it touches is an <c>EXPIRE</c>, so it renews what is there and
    /// revives nothing that has gone — including the activity, where that is precisely the property
    /// wanted (a user who cleared their game has no key left, and the tick must not put one back).
    /// </para>
    ///
    /// <para><b>Defect S16.</b> The activity key used to be written once with ten minutes and
    /// renewed by nothing at all — not this call, not the heartbeat, not the client, which dedupes
    /// identical presence and so never re-sends. Ten minutes into the same game the key lapsed under
    /// a session that had never stopped announcing it: a member arriving in the space saw no
    /// activity while everyone already there kept showing it for ever, since an expiry emits no
    /// <c>OnUserPresenceActivityRemoved</c>. Renewing it here makes the activity's lifetime the
    /// session's lifetime — the TTL demoted to a safety net for entries whose session died without
    /// finalizing, while the ordinary end of an activity stays the explicit delete
    /// (<see cref="RemoveActivityPresence"/>, which the grain's offline path already calls and which
    /// does announce the removal). Pinned by
    /// <c>PresenceAggregationTests.AnActivityIsRefreshedByTheSessionThatKeepsAnnouncingIt</c>.</para>
    ///
    /// <para><b>And the bound on it.</b> Renewing unconditionally traded one permanent state for
    /// another: the only eraser is the client's explicit <c>RemoveBroadcastPresence</c>, and a client
    /// that is killed outright — or whose removal is lost to a token refresh or a dropped connection
    /// — never sends it, so the tick kept a finished game pinned to the user for the rest of the
    /// session. The renewal is therefore a lease: a sidecar key beside the activity carries the
    /// moment its session last announced it, the client re-announces a live activity roughly every
    /// five minutes, and this renews only while that stamp is inside
    /// <see cref="PresenceTimingOptions.ActivityReassertWindow"/>. Past it the key lapses exactly as
    /// it did before. Beside the activity rather than inside it because the activity's value is a
    /// wire contract shared with the build being replaced — see
    /// <c>UserPresenceService.ActivityAnnouncedKey</c>.</para>
    ///
    /// <para><b>And the tick is the sweeper.</b> A lapse announces nothing on its own — Redis expiry
    /// has no event — so observers already holding an activity used to keep showing it for the rest of
    /// their client session while the snapshot had already forgotten it. Two people in one room then
    /// disagreed about whether a third was playing, permanently. The session that owns the entry is
    /// the one thing already running on a clock and already holding the sid, so it is the one that
    /// notices: this answers <see cref="ActivityLeaseState.Expired"/> both when the entry has gone out
    /// from under a live lease and when the lease itself has run out, and the caller then has the
    /// user's <c>IUserPresenceGrain</c> retract it once. No keyspace subscriber, no global scan, and
    /// the ordinary end of an activity is still the client's own removal.</para>
    /// </remarks>
    Task<ActivityLeaseState> RefreshSessionStatusTtlAsync(Guid userId, string sessionId, CancellationToken ct = default);

    /// <summary>
    /// Forgets the status one session reported, and nothing else.
    /// </summary>
    /// <remarks>
    /// The removal half of <see cref="SetSessionStatusAsync"/>, and it stopped recomputing the
    /// aggregate for the same reason: the fold belongs to the user's <c>IUserPresenceGrain</c>, which
    /// is the only place a user's folds are serialised against each other. A caller that deletes a
    /// session's status and does not ask the grain to re-aggregate leaves the aggregate describing a
    /// session that is gone.
    /// </remarks>
    Task RemoveSessionStatusAsync(Guid userId, string sessionId, CancellationToken ct = default);

    /// <summary>
    /// Folds every live session's status into <c>status:user:{u}:aggregated</c> and answers with what
    /// it wrote.
    /// </summary>
    /// <remarks>
    /// <para><b>Only <c>UserPresenceGrain</c> may call this.</b> It is a read-fold-write with nothing
    /// atomic about it, and the thing that makes the last write the one that saw the most recent state
    /// is that only one activation per user ever runs it — Orleans' turn-based execution, not a lock
    /// and not a script. Any second caller re-opens the lost update this replaced (a device switch
    /// leaving a connected user cached Offline until they change status by hand).</para>
    ///
    /// <para>Sessions whose presence key has gone contribute nothing and are pruned from the index on
    /// the way past, exactly as every other reader of that index does.</para>
    /// </remarks>
    Task<UserStatus> RecalculateAggregatedStatusAsync(Guid userId, CancellationToken ct = default);

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
    /// The record and the answer are one atomic write, so of N callers racing with the same status
    /// exactly one is told it changed (defect S19).
    /// </summary>
    Task<bool> MarkBroadcastIfChangedAsync(Guid userId, UserStatus status, CancellationToken ct = default);

    /// <summary>
    /// Forgets what was last broadcast for a user, so the next fan-out is treated as a change.
    /// </summary>
    /// <remarks>
    /// <para>The hysteresis record is a duplicate suppressor, and a duplicate suppressor is only
    /// correct while it describes what observers are actually holding. There is one way for those to
    /// come apart: the status keys lapse and are re-created — a socket that drops for longer than
    /// <see cref="PresenceTimingOptions.SessionTtl"/> and comes back inside the grace. Anyone who
    /// read the roster in that window cached Offline; the re-assert on re-attach repairs the keys and
    /// recomputes the aggregate, and the record still says DoNotDisturb, so the corrective broadcast
    /// is suppressed as a duplicate of something nobody received. Those observers show the user grey
    /// for the rest of their client session.</para>
    ///
    /// <para>So the repair path clears the record first and then broadcasts. Deliberately a separate
    /// call rather than a <c>force</c> flag on the fan-out: the caller that knows a lapse happened is
    /// the session grain, and everything else must keep being suppressed exactly as it is.</para>
    /// </remarks>
    Task ForgetLastBroadcastAsync(Guid userId, CancellationToken ct = default);

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

public class UserPresenceService(IArgonCacheDatabase cache, IOptions<PresenceTimingOptions> timingOptions) : IUserPresenceService
{
    /// <summary>
    /// The lifetime every presence and status key is written with, read from configuration.
    /// </summary>
    /// <remarks>
    /// Snapshotted once per singleton rather than dereferenced per call: these are the clocks the
    /// whole subsystem runs on, and a value that could change under a fold would make "the aggregate
    /// and the session key expire together" untrue for the duration of a reload.
    /// </remarks>
    private readonly PresenceTimingOptions timings = timingOptions.Value;

    /// <summary>The shipped session lifetime, for callers that still want a constant.</summary>
    /// <remarks>
    /// Kept, and kept equal to the default of <see cref="PresenceTimingOptions.SessionTtl"/> by
    /// construction, so nothing that read it before reads a different number now. Live code must not:
    /// a host that shortened the TTL — the integration suite does — would find this still saying two
    /// minutes.
    /// </remarks>
    public static readonly TimeSpan DefaultTTL = new PresenceTimingOptions().SessionTtl;

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

    /// <summary>When the session last announced the activity beside it — the lease's signature.</summary>
    /// <remarks>
    /// <para>A sidecar key rather than a field in the value, and the reason is deployment. The stamp
    /// bounds the tick's renewal (see <see cref="RefreshSessionStatusTtlAsync"/>) and it was first
    /// written by wrapping the activity in an envelope — <c>{"Presence":{…},"AnnouncedAt":"…"}</c> —
    /// which the build being replaced cannot read. Newtonsoft does not fail on it either: it binds
    /// <c>UserActivityPresence</c>'s positional constructor with defaults and hands back a non-null
    /// record with a null <c>titleName</c>, for a field the schema declares non-nullable. Through a
    /// rolling deploy every old node would then have served a phantom activity for half its roster,
    /// with the client re-announcing every few minutes to keep the supply up, and a canary would not
    /// have seen it because the damage is on the nodes that were not upgraded.</para>
    ///
    /// <para>So the activity key keeps the exact shape it has always had — a bare
    /// <c>UserActivityPresence</c>, readable by any build in either direction — and the new
    /// information lives beside it under a key an old build never looks at. The two are written,
    /// renewed and deleted together, and a stamp with no activity (or an activity with no stamp,
    /// which is every entry written before this) simply means "not renewable": the entry lapses on
    /// its own TTL exactly as it did before, and the client's next announcement writes both.</para>
    /// </remarks>
    private static string ActivityAnnouncedKey(Guid userId, string sessionId)
        => $"{ActivitySessionKey(userId, sessionId)}:announced";

    /// <summary>How long an activity survives with nothing renewing it.</summary>
    /// <remarks>
    /// A safety net rather than the activity's lifetime — see
    /// <see cref="RefreshSessionStatusTtlAsync"/>. It only decides how long an orphaned entry lingers
    /// after its session stopped keeping it alive, so it is generous on purpose.
    /// </remarks>
    private TimeSpan ActivityTTL => timings.ActivityTtl;

    // Who the session is, and when it was last heard from. Split in two because the two halves are
    // written by different things at wildly different rates: the name is a constant established once
    // at session start, the timestamp moves on every ~15s heartbeat. Folding them into one JSON blob
    // would turn each heartbeat into a read-modify-write of a value that never changes.
    private static string SessionMetaKey(Guid userId, string sessionId)
        => $"session:meta:{userId}:{sessionId}";

    private static string SessionSeenKey(Guid userId, string sessionId)
        => $"session:seen:{userId}:{sessionId}";

    // Outlives the presence TTL so a session that briefly drops off and heartbeats back keeps its
    // name instead of reappearing anonymous. Nothing reads it for a session that is not also in the
    // live index, so a stale one is invisible rather than wrong.
    private TimeSpan SessionMetaTTL => timings.SessionMetaTtl;

    public Task SetSessionOnlineAsync(Guid userId, string sessionId, CancellationToken ct = default)
        => SetSessionOnlineAsync(userId, sessionId, timings.SessionTtl, ct);

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
        await UpdateSessionAsync(userId, sessionId, timings.SessionTtl, ct);
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

    /// <summary>Records what a session is doing, and that it said so just now.</summary>
    /// <remarks>
    /// Two keys, written in the order a reader can survive: the activity first, so that a failure
    /// between the two leaves an entry that is readable and un-renewable rather than a lease with
    /// nothing under it. See <see cref="ActivityAnnouncedKey"/> for why the stamp is not part of the
    /// value.
    /// </remarks>
    public async Task BroadcastActivityPresence(UserActivityPresence presence, Guid userId, string sessionId)
    {
        await cache.StringSetAsync(
            ActivitySessionKey(userId, sessionId), JsonConvert.SerializeObject(presence), ActivityTTL);

        await cache.StringSetAsync(
            ActivityAnnouncedKey(userId, sessionId), DateTime.UtcNow.ToString("O"), ActivityTTL);
    }

    /// <summary>Reads a stored activity, or null when the entry is gone or unreadable.</summary>
    private static UserActivityPresence? ReadActivity(string? json)
    {
        if (string.IsNullOrEmpty(json))
            return null;

        try
        {
            return JsonConvert.DeserializeObject<UserActivityPresence>(json);
        }
        catch (JsonException)
        {
            return null;
        }
    }

    /// <summary>When the session behind an activity last announced it, or null if nothing says.</summary>
    /// <remarks>
    /// Null covers both "written by a build that had no stamp" and "the stamp lapsed first", and both
    /// mean the same thing to the only caller: not renewable.
    /// </remarks>
    private static DateTime? ReadAnnouncedAt(string? stamp)
        => string.IsNullOrEmpty(stamp)
            ? null
            : DateTime.TryParse(stamp, null, System.Globalization.DateTimeStyles.RoundtripKind, out var when)
                ? when.ToUniversalTime()
                : null;

    /// <summary>
    /// Every live session's activity for the user.
    /// </summary>
    /// <remarks>
    /// <para><b>Defect S18.</b> The fold used to walk the session index and read activity keys
    /// without ever asking whether the session was still alive, so an activity outlived the session
    /// announcing it: <c>presence:…:session:{sid}</c> and <c>status:…:session:{sid}</c> lapse on a
    /// two-minute clock while the activity key has ten minutes, and in the window between the two
    /// <c>SpaceReadGrain.GetPresence</c> handed clients a <c>MemberPresence</c> reading "Offline,
    /// playing Portal 2" — the state the product actually holds for up to a grace period after any
    /// ungraceful drop, and for ever when the session's grain never gets to run its grace. Checking
    /// the presence key here fixes the representative activity too, so a dead session can no longer
    /// be the one activity the single-activity wire shows. Pinned by
    /// <c>PresenceActivityTests.An_offline_member_is_never_shown_with_an_activity</c>.</para>
    ///
    /// <para>The stale sid is pruned from the index on the way past, exactly as
    /// <see cref="GetActiveSessionIdsAsync"/> and <see cref="IsUserOnlineAsync"/> already do — the
    /// index is a mirror of the presence keys and every reader that notices a divergence repairs it.
    /// That is one extra round trip per indexed session, on the same reads that already do one per
    /// session; it buys a snapshot that cannot contradict itself.</para>
    /// </remarks>
    public async Task<List<UserActivityPresence>> GetUserActivitiesAsync(Guid userId)
    {
        // Fold over the user's live sessions (same O(1) index used for status) and read each session's
        // TTL'd activity entry. Expired/empty entries contribute nothing. No keyspace SCAN.
        var activities = new List<UserActivityPresence>();
        foreach (var sessionId in await cache.SetMembersAsync(SessionsSetKey(userId)))
        {
            if (!await cache.KeyExistsAsync(SessionKey(userId, sessionId)))
            {
                await cache.SetRemoveAsync(SessionsSetKey(userId), sessionId); // prune stale
                continue;
            }

            if (ReadActivity(await cache.StringGetAsync(ActivitySessionKey(userId, sessionId))) is { } activity)
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

        // Unconditionally, whatever the activity key held: the stamp is what would let a tick renew
        // an entry, and one left behind after a clear is a lease signed for something that is gone.
        await cache.KeyDeleteAsync(ActivityAnnouncedKey(userId, sessionId));

        return existed;
    }

    /// <inheritdoc cref="IUserPresenceService.SetSessionStatusAsync"/>
    public async Task SetSessionStatusAsync(Guid userId, string sessionId, UserStatus status, CancellationToken ct = default)
    {
        var key = SessionStatusKey(userId, sessionId);
        await cache.StringSetAsync(key, status.ToString(), timings.SessionTtl, ct);
        // Ensure the session is in the live-session index so RecalculateAggregatedStatusAsync,
        // which folds over that index, always accounts for this session's status.
        await cache.SetAddAsync(SessionsSetKey(userId), sessionId, ct);
        // And no fold here. The caller re-aggregates through IUserPresenceGrain, which is the only
        // place a user's folds are ordered against each other — see the interface remarks.
    }

    /// <inheritdoc cref="IUserPresenceService.RefreshSessionStatusTtlAsync"/>
    public async Task<ActivityLeaseState> RefreshSessionStatusTtlAsync(Guid userId, string sessionId, CancellationToken ct = default)
    {
        var key = SessionStatusKey(userId, sessionId);
        await cache.UpdateStringExpirationAsync(key, timings.SessionTtl, ct);
        await cache.UpdateStringExpirationAsync(AggregatedStatusKey(userId), timings.SessionTtl, ct);

        // The activity is renewed on a lease, not for the life of the session — see the remarks on
        // the interface member. One read per tick per session, of the stamp rather than of the
        // activity: an announcement with no stamp is not renewable, so the stamp alone decides, and
        // it is the smaller of the two values. A session that announced nothing pays exactly this one
        // read and nothing below it, which is the overwhelming majority of ticks.
        if (ReadAnnouncedAt(await cache.StringGetAsync(ActivityAnnouncedKey(userId, sessionId), ct)) is not { } announcedAt)
            return ActivityLeaseState.Absent;

        // The lease has run out: the client stopped re-announcing, so the entry stops being renewed
        // exactly as it did before this renewal existed. What is new is that it is not left to lapse
        // in silence — the caller retracts it, and the retraction is what clears the two keys, so a
        // failed retraction is retried by the next tick rather than lost.
        if (DateTime.UtcNow - announcedAt >= timings.ActivityReassertWindow)
            return ActivityLeaseState.Expired;

        // A live lease over nothing: the entry was evicted, or it lapsed while its session's grain was
        // not ticking. The snapshot has already forgotten it (GetUserActivitiesAsync reads the key,
        // not the stamp) and the room has not, which is the disagreement worth an event.
        if (!await cache.KeyExistsAsync(ActivitySessionKey(userId, sessionId), ct))
            return ActivityLeaseState.Expired;

        // Both halves, together: a lease whose signature lapsed before the thing it signs for would
        // stop renewing an activity that is still being announced.
        await cache.UpdateStringExpirationAsync(ActivitySessionKey(userId, sessionId), ActivityTTL, ct);
        await cache.UpdateStringExpirationAsync(ActivityAnnouncedKey(userId, sessionId), ActivityTTL, ct);

        return ActivityLeaseState.Renewed;
    }

    /// <inheritdoc cref="IUserPresenceService.RemoveSessionStatusAsync"/>
    public Task RemoveSessionStatusAsync(Guid userId, string sessionId, CancellationToken ct = default)
        => cache.KeyDeleteAsync(SessionStatusKey(userId, sessionId), ct);

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

    /// <inheritdoc cref="IUserPresenceService.RecalculateAggregatedStatusAsync"/>
    /// <remarks>
    /// <para>The fold keeps the highest-ranked session's status, and keeps it <em>verbatim</em>: the
    /// ladder is a precedence order, not a normalisation, so a device on TouchGrass surfaces as
    /// TouchGrass rather than as "the nearest status the fold happens to recognise". The wire carries
    /// all seven members and the client has a label and a colour for each; flattening them here would
    /// leave that rendering permanently dead.</para>
    ///
    /// <para>The ladder, strongest first — <c>DoNotDisturb</c> &gt; <c>Online</c> &gt; <c>InGame</c>
    /// &gt; <c>Listen</c> &gt; <c>TouchGrass</c> &gt; <c>Away</c> &gt; <c>Offline</c>. DND is the one
    /// explicit "do not contact me" and no other device may mask it, so it short-circuits the loop;
    /// the middle of the ladder is ordered by how present the status claims the user is, and
    /// TouchGrass sits just above Away because it means the same thing said deliberately. Equal ranks
    /// keep the first session the index hands back, which is arbitrary but never wrong: the ranks are
    /// distinct for every declared member, so a tie only happens between two undeclared ones, and an
    /// undeclared member ranks at the Online tier — <c>UserStatus</c> is an open enum, a peer on a
    /// newer schema may report something this build has never heard of, and the only safe reading of
    /// "a status I do not recognise" is "present".</para>
    ///
    /// <para><b>Defect S1.</b> The old fold was three <c>if</c>s (DND/Online/Away) over a seed of
    /// <see cref="UserStatus.Offline"/>: InGame, Listen and TouchGrass matched no branch, contributed
    /// nothing, and a user whose only connected device reported one of them was written to the
    /// aggregate as Offline — invisible in every roster, dropped from the online counts, announced
    /// Offline to friends and skipped by the connect-time friends push. Pinned by
    /// <c>PresenceAggregationTests.ASingleSessionsStatusIsTheWholeAggregate</c> and its two- and
    /// three-session matrices.</para>
    ///
    /// <para><b>And why it is an ordinary application fold again.</b> It spent one release as a Lua
    /// script (<c>IArgonCacheDatabase.FoldRankedSetAsync</c>) so that the read and the write could not
    /// be interleaved by a second fold. That bought atomicity at the store and cost a deployment: the
    /// script reads keys it does not declare, which Dragonfly — the cache production actually runs —
    /// refuses by default and, with the flag that allows it, serves under a global lock, so every
    /// presence fold in the cluster would have queued behind every other. The interleaving it was
    /// guarding against is gone for a better reason now: the only caller is a per-user grain
    /// activation, and Orleans runs one turn of it at a time.</para>
    /// </remarks>
    public async Task<UserStatus> RecalculateAggregatedStatusAsync(Guid userId, CancellationToken ct = default)
    {
        var best = UserStatus.Offline;
        var at   = Rank(UserStatus.Offline);

        // Fold over this user's live sessions (O(1) index) and read each session's TTL'd status
        // string key — the source of truth — instead of SCANning the keyspace.
        foreach (var sessionId in await cache.SetMembersAsync(SessionsSetKey(userId), ct))
        {
            // A sid whose presence key has gone is a session that is over: its status key may still be
            // in its last seconds, and counting it is how a dead device kept a user Online. Pruned on
            // the way past, exactly as every other reader of this index does.
            if (!await cache.KeyExistsAsync(SessionKey(userId, sessionId), ct))
            {
                await cache.SetRemoveAsync(SessionsSetKey(userId), sessionId, ct);
                continue;
            }

            var stored = await cache.StringGetAsync(SessionStatusKey(userId, sessionId), ct);

            // Nothing stored means a session that is alive but has not said what it is yet (a hub
            // attach before the first heartbeat). It contributes nothing rather than Offline.
            if (string.IsNullOrEmpty(stored) || !Enum.TryParse<UserStatus>(stored, out var status))
                continue;

            var rank = Rank(status);

            if (rank <= at)
                continue;

            at   = rank;
            best = status;

            if (status is UserStatus.DoNotDisturb)
                break;
        }

        await cache.StringSetAsync(AggregatedStatusKey(userId), best.ToString(), timings.SessionTtl, ct);

        return best;
    }

    /// <summary>Where one status sits on the aggregation ladder. Higher wins.</summary>
    /// <remarks>
    /// The <c>default</c> arm is load-bearing and is the half that keeps defect S1 fixed: an open-enum
    /// value a newer peer sent ranks at the Online tier, so adding a member to the contract can never
    /// again make anybody vanish. It also wins <em>as itself</em> — the ladder is a precedence order,
    /// not a normalisation — so an unknown member is written to the aggregate verbatim and a client
    /// that knows it renders it.
    /// </remarks>
    private static int Rank(UserStatus status) => status switch
    {
        UserStatus.Offline      => 0,
        UserStatus.Away         => 1,
        UserStatus.TouchGrass   => 2,
        UserStatus.Listen       => 3,
        UserStatus.InGame       => 4,
        UserStatus.Online       => 5,
        UserStatus.DoNotDisturb => 6,
        _                       => 5
    };

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

    private TimeSpan LastBroadcastTTL => timings.LastBroadcastTtl;

    /// <summary>
    /// Records the status as broadcast and says whether that was a change, in one atomic step.
    /// </summary>
    /// <remarks>
    /// <para><b>Defect S19.</b> This used to be a <c>GET</c>, a comparison, and a <c>SET</c> —
    /// three separate round trips with nothing atomic between them. <c>UserGrain</c> is a
    /// <c>[StatelessWorker]</c>, so two activations of one user run this at the same instant whenever
    /// two of that user's sessions start together; both read "nothing broadcast yet", both returned
    /// true, and both fanned the same <c>UserChangedStatus</c> out to every space and every friend,
    /// writing the replay stream twice. Measured at 390 true out of 400 concurrent callers.</para>
    ///
    /// <para>The write itself now answers the question: <c>SET … EX … GET</c> returns the value this
    /// call replaced, so exactly one of N callers can see the transition and the rest see their own
    /// value already there. Pinned by
    /// <c>PresenceAggregationTests.MarkBroadcastIfChanged_UnderConcurrency_AnnouncesOnce</c> and, end
    /// to end, by <c>PresenceRaceTests.Two_devices_of_one_account_connecting_at_once_announce_the_user_online_once</c>.</para>
    ///
    /// <para>One deliberate difference from the old shape: a repeat writes the record again rather
    /// than leaving it untouched, which re-arms its 30 min lifetime. That is the better half of the
    /// trade — a status being re-asserted is evidence the record is still describing something live —
    /// and the alternative (a conditional write) is the race this method just stopped having.</para>
    /// </remarks>
    public async Task<bool> MarkBroadcastIfChangedAsync(Guid userId, UserStatus status, CancellationToken ct = default)
    {
        var key  = LastBroadcastStatusKey(userId);
        var prev = await cache.StringSetAndGetPreviousAsync(key, status.ToString(), LastBroadcastTTL, ct);

        return string.IsNullOrEmpty(prev)
            || !Enum.TryParse<UserStatus>(prev, out var prevStatus)
            || prevStatus != status;
    }

    /// <inheritdoc cref="IUserPresenceService.ForgetLastBroadcastAsync"/>
    public Task ForgetLastBroadcastAsync(Guid userId, CancellationToken ct = default)
        => cache.KeyDeleteAsync(LastBroadcastStatusKey(userId), ct);

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