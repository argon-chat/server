namespace ArgonComplexTest.Infrastructure.Presence;

using Argon.Features.Logic;
using Argon.Grains.Interfaces;
using Argon.Services;
using ArgonContracts;
using Microsoft.Extensions.DependencyInjection;
using StackExchange.Redis;

/// <summary>
/// The server side of a presence assertion: the services, the grains and the raw Redis keys the
/// product actually writes.
/// </summary>
/// <remarks>
/// <para>The client half of this campaign (<see cref="RealtimeClient"/>) sees what was broadcast.
/// That is not enough on its own, because most of the presence defects are a disagreement between
/// the broadcast and the stored state: a user shown Online whose aggregate says Offline, a session
/// whose status key stopped being refreshed while the connection is still up, an activity that
/// lapsed under a game that never stopped. Reading the stored state is the only way to name which
/// of the two is wrong.</para>
///
/// <para>Both routes into Redis are here on purpose. <see cref="Presence"/> and <see cref="Cache"/>
/// are the product's own abstractions and are what a test should prefer — they cannot drift from the
/// server's idea of a key. <see cref="Redis"/> is the raw connection, and it exists for the two
/// things the abstraction cannot express: reading a TTL (there is no such method on
/// <see cref="IArgonCacheDatabase"/>, and TTL is the whole evidence for "this key is no longer being
/// refreshed"), and shortening one so a 120-second lapse can be observed inside a test's budget
/// rather than being slept through.</para>
///
/// <para>It is the <c>Cache</c> profile on logical database 0, which is where
/// <c>UserPresenceService</c> writes; nothing in the cache path prefixes its keys, so the builders
/// below are the literal key names.</para>
/// </remarks>
public sealed class PresenceProbe
{
    // One multiplexer per test process rather than one per fixture: every fixture that touches
    // presence would otherwise open its own, and Testcontainers' Redis is not there to measure
    // connection churn.
    private static readonly Lazy<Task<IConnectionMultiplexer>> Shared = new(OpenAsync);

    private static async Task<IConnectionMultiplexer> OpenAsync()
        => await ConnectionMultiplexer.ConnectAsync(
            ArgonTestEnvironment.Instance.Host.Settings.RedisConnectionString);

    private PresenceProbe(IServiceProvider services, IDatabase redis)
    {
        Presence = services.GetRequiredService<IUserPresenceService>();
        Grains   = services.GetRequiredService<IGrainFactory>();
        Cache    = services.GetRequiredService<IArgonCacheDatabase>();
        Redis    = redis;
    }

    /// <summary>Builds a probe against the running host, connecting the raw Redis client if needed.</summary>
    /// <remarks>
    /// Cheap enough to call per test; the underlying multiplexer is shared and connected once.
    /// </remarks>
    public static async Task<PresenceProbe> CreateAsync(CancellationToken ct = default)
    {
        var multiplexer = await Shared.Value.WaitAsync(ct);
        return new PresenceProbe(ArgonTestEnvironment.Instance.Host.Services, multiplexer.GetDatabase(CacheDatabaseIndex));
    }

    /// <summary>
    /// The logical Redis database the <c>Cache</c> profile resolves to in the test host — see
    /// <c>ArgonServerTargetHost</c>, which maps every profile onto the one container by index.
    /// </summary>
    public const int CacheDatabaseIndex = 0;

    /// <summary>The service every grain writes presence through.</summary>
    public IUserPresenceService Presence { get; }

    /// <summary>The cluster's grain factory, for driving a session or a space directly.</summary>
    public IGrainFactory Grains { get; }

    /// <summary>The product's cache abstraction — same keys, same values, no TTL reads.</summary>
    public IArgonCacheDatabase Cache { get; }

    /// <summary>The raw connection, for TTLs and for forcing an expiry.</summary>
    public IDatabase Redis { get; }

    /// <summary>
    /// The presence clocks the host is running on — TTLs, tick, grace, deadlines.
    /// </summary>
    /// <remarks>
    /// The integration host runs the shipped presence code against compressed timings
    /// (<see cref="TestPresenceTimings"/>), so a fixture that wrote "wait 35 seconds" would be
    /// asserting against a number nothing uses. Every wait is derived from this instead — see
    /// <see cref="PresenceWaits"/>, which holds the derivations themselves; this property is here for
    /// the raw values a test wants to name directly, such as the TTL a key was written with.
    /// </remarks>
    public PresenceTimingOptions Timings => PresenceWaits.Timings;

    // ---------------------------------------------------------------------------------------------
    // Grains
    // ---------------------------------------------------------------------------------------------

    /// <summary>The session grain for one sid — the key <c>AppHub</c> builds, <c>"{userId}:{sid}"</c>.</summary>
    /// <remarks>
    /// Addressing it by hand is how a test performs a detach or an attach without a transport, and
    /// how it reaches <c>GoOfflineAsync</c> for a session whose client has already gone.
    /// </remarks>
    public IUserSessionGrain SessionGrain(Guid userId, string sid)
        => Grains.GetGrain<IUserSessionGrain>($"{userId}:{sid}");

    /// <inheritdoc cref="SessionGrain(System.Guid,string)"/>
    public IUserSessionGrain SessionGrain(Guid userId, Guid sid)
        => SessionGrain(userId, sid.ToString());

    /// <summary>The session grain behind a test session's own client.</summary>
    public IUserSessionGrain SessionGrain(TestUserSession session)
        => SessionGrain(session.UserId, session.SessionId);

    /// <summary>The user grain — the aggregator and the friends fan-out.</summary>
    public IUserGrain UserGrain(Guid userId)
        => Grains.GetGrain<IUserGrain>(userId);

    /// <summary>The space grain — the per-space status fan-out.</summary>
    public ISpaceGrain SpaceGrain(Guid spaceId)
        => Grains.GetGrain<ISpaceGrain>(spaceId);

    /// <summary>The read-side space grain, which is where <c>GetPresence</c> and its 1 s cache live.</summary>
    public ISpaceReadGrain SpaceReadGrain(Guid spaceId)
        => Grains.GetGrain<ISpaceReadGrain>(spaceId);

    // ---------------------------------------------------------------------------------------------
    // Raw Redis
    // ---------------------------------------------------------------------------------------------

    /// <summary>How much life the key has left, or null if it is gone or has no expiry.</summary>
    /// <remarks>
    /// The distinction that matters most in this campaign: a presence or status key is written with a
    /// 120 s TTL and pushed back to 120 s by the session's 15 s tick, so a TTL comfortably under 120
    /// is direct evidence that nothing is refreshing it any more — which is the failure mode behind
    /// several of the status bugs, and one that no snapshot API can show.
    /// </remarks>
    public Task<TimeSpan?> TtlOf(string key)
        => Redis.KeyTimeToLiveAsync(key);

    /// <summary>Shortens a key's TTL so a lapse that would take two minutes can be watched in seconds.</summary>
    /// <remarks>
    /// Not a shortcut around an assertion: the thing under test is what the server does when the key
    /// lapses, and it does the same thing whether the lapse took 120 seconds or two. Returns false if
    /// the key was not there to expire, which is usually a sign the test is looking at the wrong sid.
    /// </remarks>
    public Task<bool> ForceExpire(string key, TimeSpan within)
        => Redis.KeyExpireAsync(key, within);

    /// <summary>Whether the key exists at all.</summary>
    public Task<bool> Exists(string key)
        => Redis.KeyExistsAsync(key);

    /// <summary>The raw string value of a key, or null.</summary>
    public async Task<string?> ValueOf(string key)
        => await Redis.StringGetAsync(key);

    // ---------------------------------------------------------------------------------------------
    // Key builders — mirroring UserPresenceService, which keeps them private
    // ---------------------------------------------------------------------------------------------

    /// <summary>
    /// <c>presence:user:{u}:session:{sid}</c> — the 120 s TTL'd fact that a session is alive. A
    /// session exists exactly while this key does.
    /// </summary>
    public static string PresenceSessionKey(Guid userId, string sid)
        => $"presence:user:{userId}:session:{sid}";

    /// <inheritdoc cref="PresenceSessionKey(System.Guid,string)"/>
    public static string PresenceSessionKey(Guid userId, Guid sid)
        => PresenceSessionKey(userId, sid.ToString());

    /// <summary>
    /// <c>presence:user:{u}:sessions</c> — the O(1) SET index of the user's sids, pruned lazily
    /// against the keys above rather than kept in step with them.
    /// </summary>
    public static string PresenceSessionsSetKey(Guid userId)
        => $"presence:user:{userId}:sessions";

    /// <summary>
    /// <c>status:user:{u}:session:{sid}</c> — one session's status name, 120 s, refreshed only by the
    /// grain's tick or by a status change.
    /// </summary>
    public static string SessionStatusKey(Guid userId, string sid)
        => $"status:user:{userId}:session:{sid}";

    /// <inheritdoc cref="SessionStatusKey(System.Guid,string)"/>
    public static string SessionStatusKey(Guid userId, Guid sid)
        => SessionStatusKey(userId, sid.ToString());

    /// <summary>
    /// <c>status:user:{u}:aggregated</c> — the folded status every snapshot reads, recomputed only by
    /// a session status set or removal.
    /// </summary>
    public static string AggregatedStatusKey(Guid userId)
        => $"status:user:{userId}:aggregated";

    /// <summary>
    /// <c>status:user:{u}:lastbroadcast</c> — the hysteresis record, 30 min. A status equal to this
    /// one is not fanned out at all, so a stale value here silently swallows a real change.
    /// </summary>
    public static string LastBroadcastStatusKey(Guid userId)
        => $"status:user:{userId}:lastbroadcast";

    /// <summary>
    /// <c>activity:user:{u}:session:{sid}</c> — the JSON activity for one session, 10 min, refreshed
    /// by nothing.
    /// </summary>
    public static string ActivitySessionKey(Guid userId, string sid)
        => $"activity:user:{userId}:session:{sid}";

    /// <inheritdoc cref="ActivitySessionKey(System.Guid,string)"/>
    public static string ActivitySessionKey(Guid userId, Guid sid)
        => ActivitySessionKey(userId, sid.ToString());

    /// <summary>
    /// <c>session:meta:{u}:{sid}</c> — who the session is, written once by <c>PickTicket</c>, 24 h.
    /// Note the shape: no <c>session:</c> segment before the sid, unlike the presence keys.
    /// </summary>
    public static string SessionMetaKey(Guid userId, string sid)
        => $"session:meta:{userId}:{sid}";

    /// <inheritdoc cref="SessionMetaKey(System.Guid,string)"/>
    public static string SessionMetaKey(Guid userId, Guid sid)
        => SessionMetaKey(userId, sid.ToString());

    /// <summary><c>session:seen:{u}:{sid}</c> — the last heartbeat stamp behind the devices screen, 24 h.</summary>
    public static string SessionSeenKey(Guid userId, string sid)
        => $"session:seen:{userId}:{sid}";

    /// <inheritdoc cref="SessionSeenKey(System.Guid,string)"/>
    public static string SessionSeenKey(Guid userId, Guid sid)
        => SessionSeenKey(userId, sid.ToString());

    // ---------------------------------------------------------------------------------------------
    // Readers
    // ---------------------------------------------------------------------------------------------

    /// <summary>The user's folded status, as every snapshot API reads it.</summary>
    public Task<UserStatus> AggregatedStatusAsync(Guid userId, CancellationToken ct = default)
        => Presence.GetAggregatedStatusAsync(userId, ct);

    /// <summary>
    /// One session's own status, or null when the key has lapsed.
    /// </summary>
    /// <remarks>
    /// Read raw rather than through the service because null and <see cref="UserStatus.Offline"/> are
    /// different answers here: the aggregate reports Offline for a user whose keys have gone, and the
    /// question a TTL test asks is precisely whether the key is still there.
    /// </remarks>
    public async Task<UserStatus?> SessionStatusAsync(Guid userId, string sid)
    {
        var raw = await ValueOf(SessionStatusKey(userId, sid));
        return Enum.TryParse<UserStatus>(raw, out var status) ? status : null;
    }

    /// <inheritdoc cref="SessionStatusAsync(System.Guid,string)"/>
    public Task<UserStatus?> SessionStatusAsync(Guid userId, Guid sid)
        => SessionStatusAsync(userId, sid.ToString());

    /// <summary>The last status actually fanned out for this user, or null if the record has lapsed.</summary>
    public async Task<UserStatus?> LastBroadcastAsync(Guid userId)
    {
        var raw = await ValueOf(LastBroadcastStatusKey(userId));
        return Enum.TryParse<UserStatus>(raw, out var status) ? status : null;
    }

    /// <summary>Whether any session of the user still has a live presence key.</summary>
    public Task<bool> IsUserOnlineAsync(Guid userId, CancellationToken ct = default)
        => Presence.IsUserOnlineAsync(userId, ct);

    /// <summary>Whether this one session's presence key is still there.</summary>
    public Task<bool> IsSessionAliveAsync(Guid userId, string sid, CancellationToken ct = default)
        => Presence.IsSessionAliveAsync(userId, sid, ct);

    /// <inheritdoc cref="IsSessionAliveAsync(System.Guid,string,System.Threading.CancellationToken)"/>
    public Task<bool> IsSessionAliveAsync(Guid userId, Guid sid, CancellationToken ct = default)
        => Presence.IsSessionAliveAsync(userId, sid.ToString(), ct);

    /// <summary>The sids the live-session index claims, whether or not their presence keys survive.</summary>
    /// <remarks>
    /// Deliberately the unpruned SET: the gap between what the index says and what the presence keys
    /// say is itself a defect surface, so a test that wants the reconciled answer asks
    /// <see cref="ActiveSessionIdsAsync"/> instead.
    /// </remarks>
    public async Task<string[]> SessionIndexAsync(Guid userId)
        => (await Redis.SetMembersAsync(PresenceSessionsSetKey(userId))).Select(x => x.ToString()).ToArray();

    /// <summary>The sids that are both in the index and still have a live presence key.</summary>
    public Task<List<string>> ActiveSessionIdsAsync(Guid userId, CancellationToken ct = default)
        => Presence.GetActiveSessionIdsAsync(userId, ct);

    /// <summary>The representative activity across the user's live sessions, or null.</summary>
    public Task<UserActivityPresence?> ActivityAsync(Guid userId)
        => Presence.GetUsersActivityPresence(userId);

    /// <summary>The devices-screen record for one session, or null when it was never written.</summary>
    public Task<UserSessionMeta?> SessionMetaAsync(Guid userId, string sid, CancellationToken ct = default)
        => Presence.GetSessionMetaAsync(userId, sid, ct);

    /// <inheritdoc cref="SessionMetaAsync(System.Guid,string,System.Threading.CancellationToken)"/>
    public Task<UserSessionMeta?> SessionMetaAsync(Guid userId, Guid sid, CancellationToken ct = default)
        => Presence.GetSessionMetaAsync(userId, sid.ToString(), ct);

    // ---------------------------------------------------------------------------------------------
    // Waits
    // ---------------------------------------------------------------------------------------------

    /// <summary>
    /// Polls the aggregate until it is <paramref name="expected"/> or the timeout is spent, and
    /// returns whatever it last read so the assertion can name it.
    /// </summary>
    public Task<UserStatus> WaitForAggregatedStatusAsync(
        Guid userId, UserStatus expected, TimeSpan timeout, CancellationToken ct = default)
        => Poll.ForValueAsync(() => AggregatedStatusAsync(userId, ct), s => s == expected, timeout, ct: ct);

    /// <summary>Polls until the user has no live session left, or the timeout is spent.</summary>
    public Task<bool> WaitUntilOfflineAsync(Guid userId, TimeSpan timeout, CancellationToken ct = default)
        => Poll.UntilAsync(async () => !await IsUserOnlineAsync(userId, ct), timeout, ct: ct);

    /// <summary>Polls until one session's presence key is gone, or the timeout is spent.</summary>
    public Task<bool> WaitUntilSessionGoneAsync(
        Guid userId, string sid, TimeSpan timeout, CancellationToken ct = default)
        => Poll.UntilAsync(async () => !await IsSessionAliveAsync(userId, sid, ct), timeout, ct: ct);
}
