namespace ArgonComplexTest;

using Argon.Features.Logic;
using ArgonContracts;
using Argon.Services;
using Microsoft.Extensions.DependencyInjection;
using StackExchange.Redis;

/// <summary>
/// The presence layer on its own, against the real Redis it is written for.
/// </summary>
/// <remarks>
/// <para>Everything above this file — the session grain, the hub, the space fan-out — reads the
/// user's status out of <see cref="IUserPresenceService"/> and believes it. So a bug here is not one
/// bug: it is every screen that shows a dot next to a name. This fixture talks to the service and to
/// nothing else, with random <see cref="Guid"/> users, because at this layer a user is only a key
/// prefix — no registration, no grain, no connection. That makes the failures unambiguous: when a
/// test here is red, nothing above it can be blamed.</para>
///
/// <para><b>The intended aggregation.</b> The service folds every live session's status into one
/// value, and the fold keeps the winning session's status verbatim rather than normalising it. The
/// ladder is the one <c>UserPresenceService.RecalculateAggregatedStatusAsync</c> now documents and
/// implements, strongest first:</para>
/// <list type="number">
/// <item><description><c>DoNotDisturb</c> — the strongest explicit "do not contact me". It must never
/// be masked by another device, and it is the only value that short-circuits the fold.</description></item>
/// <item><description><c>Online</c>, then <c>InGame</c>, then <c>Listen</c> — the present tier,
/// ordered by how plainly the status claims the user is at a keyboard. They are distinct rather than
/// tied, so the answer never depends on the order Redis returns the session set in.</description></item>
/// <item><description><c>TouchGrass</c> — "away from this, deliberately". It sits just above
/// <c>Away</c> because it means the same thing said on purpose, and below the present tier because a
/// device that is genuinely being used is the more informative answer about the account.</description></item>
/// <item><description><c>Away</c> — derived from idleness rather than chosen.</description></item>
/// <item><description><c>Offline</c> — the floor, and reachable only when every session says so.</description></item>
/// </list>
///
/// <para>The one claim that needs no tier argument at all, and the one hypothesis H3 is really about:
/// <b>a session that is connected and reporting a non-Offline status must never aggregate to
/// Offline.</b> The fold used to recognise three of the seven enum members, so a user whose only
/// device said TouchGrass, InGame or Listen read Offline everywhere — the space roster, the friends
/// push, the member snapshot. Those failures are still listed separately from the priority ones
/// below, so a future re-tiering can never be confused with a value the fold dropped.</para>
///
/// <para>The other theme is the pair of keys behind one session — <c>presence:…:session:{sid}</c> and
/// <c>status:…:session:{sid}</c>. They carry the same 120 s TTL and are re-armed by the same 15 s
/// tick, status first and presence last, so in production they live and lapse together; what differs
/// is which reader consults which. <c>IsUserOnlineAsync</c> answers from the presence key and the
/// aggregation fold from the status key — that is the fold's liveness criterion, unchanged since it
/// was a keyspace <c>SCAN</c> over those keys — and the tests below pin that the two never disagree
/// about a user in a state the product can actually reach. Both refreshers are plain <c>EXPIRE</c>s
/// and neither recreates a key that has gone. That is deliberate: a lapsed presence key is how the
/// grace, finalize and revocation paths know a device is gone, so the repair for a lapse is the
/// reconnect, never the keep-alive.</para>
/// </remarks>
[TestFixture]
public class PresenceAggregationTests : TestBase
{
    /// <summary>Long enough that no test races its own fixtures, short enough to leave Redis tidy.</summary>
    private static readonly TimeSpan SessionTtl = TimeSpan.FromSeconds(60);

    /// <summary>How long a forced lapse is given to actually happen before the test gives up.</summary>
    private static readonly TimeSpan LapseBudget = TimeSpan.FromSeconds(10);

    private IUserPresenceService Presence     => FactoryAsp.Services.GetRequiredService<IUserPresenceService>();
    private IArgonCacheDatabase  Cache        => FactoryAsp.Services.GetRequiredService<IArgonCacheDatabase>();

    /// <summary>
    /// The concrete service, for its TTL-taking <c>SetSessionOnlineAsync</c> overload — the interface
    /// only offers the 120 s default, and a test that waited that out would be a test nobody runs.
    /// </summary>
    private UserPresenceService PresenceImpl => (UserPresenceService)Presence;

    /// <summary>
    /// The keys, mirrored from <c>UserPresenceService</c> where they are private.
    /// </summary>
    /// <remarks>
    /// Copied rather than exposed: a test that reaches for a key the service does not actually write
    /// fails loudly here (the state it sets up has no effect), which is the same protection making
    /// the members internal would give, without widening the service's surface for a test's sake.
    /// </remarks>
    private static class Keys
    {
        public static string Session(Guid userId, string sid)       => $"presence:user:{userId}:session:{sid}";
        public static string Sessions(Guid userId)                  => $"presence:user:{userId}:sessions";
        public static string SessionStatus(Guid userId, string sid) => $"status:user:{userId}:session:{sid}";
        public static string Aggregated(Guid userId)                => $"status:user:{userId}:aggregated";
        public static string LastBroadcast(Guid userId)             => $"status:user:{userId}:lastbroadcast";
        public static string Activity(Guid userId, string sid)      => $"activity:user:{userId}:session:{sid}";
        public static string Meta(Guid userId, string sid)          => $"session:meta:{userId}:{sid}";
        public static string Seen(Guid userId, string sid)          => $"session:seen:{userId}:{sid}";
    }

    /// <summary>The live TTL of a key, straight off the connection the service itself writes through.</summary>
    private async Task<TimeSpan?> TtlOfAsync(string key)
    {
        var pool = FactoryAsp.Services.GetRequiredKeyedService<IRedisPoolConnections>(RedisProfiles.Cache);

        await using var scope = pool.Rent();

        return await scope.GetDatabase().KeyTimeToLiveAsync(key);
    }

    /// <summary>Polls to a deadline. Never a fixed sleep — a slow container must not decide a verdict.</summary>
    private static async Task WaitUntilAsync(Func<Task<bool>> condition, string what, CancellationToken ct)
    {
        var deadline = DateTime.UtcNow + LapseBudget;

        while (DateTime.UtcNow < deadline)
        {
            if (await condition())
                return;
            await Task.Delay(50, ct);
        }

        Assert.Fail($"timed out after {LapseBudget.TotalSeconds:F0}s waiting for {what}");
    }

    /// <summary>Forces a key to lapse now, and waits until Redis agrees it is gone.</summary>
    private async Task LapseAsync(string key, string what, CancellationToken ct)
    {
        await Cache.UpdateStringExpirationAsync(key, TimeSpan.FromMilliseconds(200), ct);
        await WaitUntilAsync(async () => !await Cache.KeyExistsAsync(key, ct), what, ct);
    }

    // ---------------------------------------------------------------------------------------------
    // The aggregation matrix
    // ---------------------------------------------------------------------------------------------

    private static readonly UserStatus[] AllStatuses = Enum.GetValues<UserStatus>();

    /// <summary>The intended precedence tier of a status. See the fixture remarks for the reasoning.</summary>
    /// <remarks>
    /// Mirrored from <c>UserPresenceService.Precedence</c>, deliberately by hand: a test that imported
    /// the product's ladder would agree with it however it changed and would guard nothing.
    /// </remarks>
    private static int Tier(UserStatus status) => status switch
    {
        UserStatus.DoNotDisturb => 6,
        UserStatus.Online       => 5,
        UserStatus.InGame       => 4,
        UserStatus.Listen       => 3,
        UserStatus.TouchGrass   => 2,
        UserStatus.Away         => 1,
        _                       => 0 // Offline
    };

    /// <summary>
    /// Every aggregate a set of reported statuses may legitimately produce: the reported members of
    /// the highest tier present. The ladder is a total order over the declared members, so this is a
    /// single status; it stays a set so that a future tie (an undeclared member a newer peer sent,
    /// which the fold ranks at the Online tier) does not need the assertions rewritten.
    /// </summary>
    private static UserStatus[] Acceptable(IReadOnlyList<UserStatus> reported)
    {
        var top = reported.Max(Tier);
        return reported.Where(s => Tier(s) == top).Distinct().ToArray();
    }

    /// <summary>Puts one live session per reported status on a fresh user and reads the aggregate.</summary>
    private async Task<UserStatus> AggregateOfAsync(IReadOnlyList<UserStatus> reported, CancellationToken ct)
    {
        var userId = Guid.NewGuid();

        for (var i = 0; i < reported.Count; i++)
        {
            var sid = $"sid-{i}";
            // Both halves of a live session, in the order the session grain writes them.
            await PresenceImpl.SetSessionOnlineAsync(userId, sid, SessionTtl, ct);
            await Presence.SetSessionStatusAsync(userId, sid, reported[i], ct);
        }

        return await Presence.GetAggregatedStatusAsync(userId, ct);
    }

    private static void Classify(
        IReadOnlyList<UserStatus> reported,
        UserStatus                actual,
        List<string>              wentOffline,
        List<string>              wrongPriority)
    {
        var acceptable = Acceptable(reported);
        if (acceptable.Contains(actual))
            return;

        var text = $"[{string.Join(" + ", reported)}] -> {actual} (expected {string.Join(" or ", acceptable)})";

        if (actual is UserStatus.Offline)
            wentOffline.Add(text);
        else
            wrongPriority.Add(text);
    }

    /// <summary>
    /// One connected session, seven statuses: the aggregate is what that session said it was.
    /// </summary>
    /// <remarks>
    /// With a single session there is nothing to fold and no precedence to argue about — the answer
    /// can only be the status the session reported. That makes this the cleanest statement of H3:
    /// a user whose one device says TouchGrass (a status the desktop client persists across
    /// restarts), InGame or Listen must not read Offline to the rest of the product.
    ///
    /// <para>The contract it guards (defect S1, fixed): every member of <c>UserStatus</c> survives
    /// the fold as itself. <c>RecalculateAggregatedStatusAsync</c> ranks statuses rather than
    /// matching three of them by name, and keeps the winner verbatim, so no value the contract
    /// defines — and no value a newer peer invents, which ranks at the Online tier — can be turned
    /// into Offline by a fold that simply did not recognise it.</para>
    /// </remarks>
    [Test, CancelAfter(120_000)]
    public async Task ASingleSessionsStatusIsTheWholeAggregate(CancellationToken ct = default)
    {
        var wentOffline   = new List<string>();
        var wrongPriority = new List<string>();

        foreach (var status in AllStatuses)
            Classify([status], await AggregateOfAsync([status], ct), wentOffline, wrongPriority);

        Assert.Multiple(() =>
        {
            Assert.That(wentOffline, Is.Empty,
                $"{wentOffline.Count} of {AllStatuses.Length} statuses read as Offline on a connected session — "
              + "a user the product will show as offline everywhere while their client is connected and saying otherwise");
            Assert.That(wrongPriority, Is.Empty,
                "a single session cannot be outvoted; the aggregate has to be its own status");
        });
    }

    /// <summary>
    /// Two devices, every pair of statuses: the stronger one wins, and neither device can silence the other.
    /// </summary>
    /// <remarks>
    /// Same-status pairs are included deliberately — "Away on both" reading as anything but Away, or
    /// "TouchGrass on both" reading as Offline, is the multi-device shape of the same fold bug.
    ///
    /// <para>The contract it guards (defect S1, fixed): for every one of the 28 pairs the aggregate
    /// is the higher of the two on the fixture's ladder, and no pair of connected devices reads
    /// Offline. The pairs that used to resolve to the weaker device (Away+InGame, Away+Listen,
    /// Away+TouchGrass) were the same dropped-enum-member bug seen from the side where a weaker
    /// status happened to survive the fold.</para>
    /// </remarks>
    [Test, CancelAfter(120_000)]
    public async Task TwoSessions_AggregateToTheStrongerStatus(CancellationToken ct = default)
    {
        var wentOffline   = new List<string>();
        var wrongPriority = new List<string>();
        var combinations  = 0;

        for (var i = 0; i < AllStatuses.Length; i++)
        for (var j = i; j < AllStatuses.Length; j++)
        {
            UserStatus[] pair = [AllStatuses[i], AllStatuses[j]];
            combinations++;
            Classify(pair, await AggregateOfAsync(pair, ct), wentOffline, wrongPriority);
        }

        Assert.Multiple(() =>
        {
            Assert.That(wentOffline, Is.Empty,
                $"{wentOffline.Count} of {combinations} pairs left a user with two connected devices reading Offline");
            Assert.That(wrongPriority, Is.Empty,
                $"{wrongPriority.Count} of {combinations} pairs resolved to the weaker device's status");
        });
    }

    /// <summary>
    /// Three devices, every distinct triple: still the strongest status present.
    /// </summary>
    /// <remarks>
    /// The fold breaks out of its loop on DoNotDisturb and otherwise depends on the order Redis hands
    /// back the session set, which is unordered. A third session is what makes an order-dependent
    /// fold visible: a rule that happens to be order-independent for two members need not stay so.
    ///
    /// <para>The contract it guards (defect S1, fixed): the ladder is a total order over the seven
    /// declared members, so every one of the 35 triples has exactly one right answer whatever order
    /// the session set is walked in — including the DoNotDisturb short-circuit, which must win from
    /// every position rather than only when it is seen first.</para>
    /// </remarks>
    [Test, CancelAfter(120_000)]
    public async Task ThreeSessions_AggregateToTheStrongestStatus(CancellationToken ct = default)
    {
        var wentOffline   = new List<string>();
        var wrongPriority = new List<string>();
        var combinations  = 0;

        for (var i = 0; i < AllStatuses.Length; i++)
        for (var j = i + 1; j < AllStatuses.Length; j++)
        for (var k = j + 1; k < AllStatuses.Length; k++)
        {
            UserStatus[] triple = [AllStatuses[i], AllStatuses[j], AllStatuses[k]];
            combinations++;
            Classify(triple, await AggregateOfAsync(triple, ct), wentOffline, wrongPriority);
        }

        Assert.Multiple(() =>
        {
            Assert.That(wentOffline, Is.Empty,
                $"{wentOffline.Count} of {combinations} triples left a user with three connected devices reading Offline");
            Assert.That(wrongPriority, Is.Empty,
                $"{wrongPriority.Count} of {combinations} triples resolved to a weaker device's status");
        });
    }

    // ---------------------------------------------------------------------------------------------
    // The two keys behind one session
    // ---------------------------------------------------------------------------------------------

    /// <summary>
    /// A session whose status key lapsed stops voting, and it is the reconnect — not the keep-alive —
    /// that puts it back.
    /// </summary>
    /// <remarks>
    /// <para>The fold's liveness criterion is the status key's own 120 s TTL, and it has been that
    /// since before the session index existed: <c>RecalculateAggregatedStatusAsync</c> used to
    /// <c>SCAN</c> <c>status:user:{u}:session:*</c> directly, and the index replaced the scan without
    /// changing the rule — a session whose status key has expired returns nothing and contributes
    /// nothing, exactly as the old scan did. So a session with no status key has no vote by design,
    /// not by omission.</para>
    ///
    /// <para>The keep-alive pair is <c>EXPIRE</c>-only on purpose. The lapse of a presence key is the
    /// system's "this device is gone" signal — <c>DetachConnectionAsync</c> stops refreshing so the
    /// TTL can lapse, the grace reminder lets it lapse, and <c>SecurityGrain.EndSessionAsync</c>
    /// leans on it ("presence will lapse on its own TTL within two minutes") — so a keep-alive that
    /// recreated keys would put revoked sessions back on the air underneath every grain-side check.
    /// It could not restore the status key in any case: only the grain knows what the status is.</para>
    ///
    /// <para>What repairs a lapse is the reconnect. <c>UserSessionGrain.AttachConnectionAsync</c>
    /// calls <c>SetSessionOnlineAsync</c> unconditionally so "a brief lapse self-heals", and the
    /// grain re-asserts its own status alongside it. Both halves are pinned below: the keep-alive
    /// does not resurrect, the reconnect does. (Open design question S21: if the product ever wants a
    /// self-healing keep-alive it belongs in <c>UserSessionGrain.UserSessionTickAsync</c>, gated on
    /// the revocation tombstone, never in the service's EXPIRE primitives.)</para>
    /// </remarks>
    [Test, CancelAfter(120_000)]
    public async Task ASessionWhoseStatusKeyLapsed_IsRepairedByReconnecting_NotByTheKeepAlive(CancellationToken ct = default)
    {
        var userId = Guid.NewGuid();
        var live   = $"live-{Guid.NewGuid():N}";
        var other  = $"other-{Guid.NewGuid():N}";

        await PresenceImpl.SetSessionOnlineAsync(userId, live, SessionTtl, ct);
        await Presence.SetSessionStatusAsync(userId, live, UserStatus.Online, ct);

        Assert.That(await Presence.GetAggregatedStatusAsync(userId, ct), Is.EqualTo(UserStatus.Online),
            "precondition: one connected session reporting Online");

        await LapseAsync(Keys.SessionStatus(userId, live), "the session's status key to lapse", ct);

        // Everything the keep-alive path does. Both calls are EXPIREs; neither can put back a key.
        for (var i = 0; i < 3; i++)
        {
            await Presence.RefreshSessionStatusTtlAsync(userId, live, ct);
            await Presence.HeartbeatAsync(userId, live, ct);
        }

        // A second device connects and goes again — the ordinary event that recalculates the fold.
        await PresenceImpl.SetSessionOnlineAsync(userId, other, SessionTtl, ct);
        await Presence.SetSessionStatusAsync(userId, other, UserStatus.Online, ct);
        await Presence.RemoveSessionStatusAsync(userId, other, ct);
        await Presence.RemoveSessionAsync(userId, other, ct);

        var afterKeepAlive = await Presence.GetAggregatedStatusAsync(userId, ct);
        var statusRecreated = await Cache.KeyExistsAsync(Keys.SessionStatus(userId, live), ct);
        var presenceAlive   = await Presence.IsSessionAliveAsync(userId, live, ct);

        // The reconnect: what AttachConnectionAsync does, plus the grain re-asserting its status.
        await PresenceImpl.SetSessionOnlineAsync(userId, live, SessionTtl, ct);
        await Presence.SetSessionStatusAsync(userId, live, UserStatus.Online, ct);

        var afterReconnect = await Presence.GetAggregatedStatusAsync(userId, ct);

        Assert.Multiple(() =>
        {
            Assert.That(presenceAlive, Is.True,
                "the presence key was kept alive throughout — an EXPIRE on a key that still exists does work");
            Assert.That(statusRecreated, Is.False,
                "the keep-alive recreated a status key it is documented never to create");
            Assert.That(afterKeepAlive, Is.EqualTo(UserStatus.Offline),
                "a session with no status key carries no vote: the fold's liveness criterion is that key's own TTL");
            Assert.That(afterReconnect, Is.EqualTo(UserStatus.Online),
                "and reconnecting — the repair the grain performs on every attach — did not bring the session back");
        });
    }

    /// <summary>
    /// A session that stops being refreshed lapses out of the aggregate and out of the online flag
    /// together.
    /// </summary>
    /// <remarks>
    /// <para>The two keys behind one session are written with the same 120 s TTL by every production
    /// writer and re-armed by the same 15 s tick, status first and presence last
    /// (<c>UserSessionGrain.UserSessionTickAsync</c> calls <c>RefreshSessionStatusTtlAsync</c> and
    /// then <c>HeartbeatAsync</c>), so the presence key never expires before the status key. A silo
    /// stall, a crash, or anything else that stops the tick therefore lapses both — and
    /// <c>GetAggregatedStatusAsync</c> and <c>IsUserOnlineAsync</c>, which answer from different
    /// keys, have to end up saying the same thing about the same user.</para>
    ///
    /// <para>This is the reachable shape of the question S20 raised. The fold does not consult the
    /// presence key and never has; manufacturing "status alive, presence dead" takes the test-only
    /// <c>SetSessionOnlineAsync(ttl)</c> overload, because no production writer gives the two keys
    /// different lifetimes. What a reader can actually observe is what is asserted here.</para>
    /// </remarks>
    [Test, CancelAfter(120_000)]
    public async Task ASessionThatStopsBeingRefreshed_LapsesOutOfTheAggregateAndTheOnlineFlagTogether(
        CancellationToken ct = default)
    {
        var userId = Guid.NewGuid();
        var sid    = $"stalled-{Guid.NewGuid():N}";

        await PresenceImpl.SetSessionOnlineAsync(userId, sid, SessionTtl, ct);
        await Presence.SetSessionStatusAsync(userId, sid, UserStatus.Online, ct);

        Assert.That(await Presence.GetAggregatedStatusAsync(userId, ct), Is.EqualTo(UserStatus.Online),
            "precondition: the session is live and voting");

        // The tick stops. Both keys drain, and nothing re-arms them: both refreshers are EXPIREs.
        await LapseAsync(Keys.SessionStatus(userId, sid), "the session's status key to lapse", ct);
        await LapseAsync(Keys.Session(userId, sid), "the session's presence key to lapse", ct);

        // Anything at all recalculating the fold — here, some other session of the same user ending.
        await Presence.RemoveSessionStatusAsync(userId, $"never-existed-{Guid.NewGuid():N}", ct);

        // Read the aggregate BEFORE IsUserOnlineAsync: that call prunes the index as a side effect,
        // and the claim is that the fold is already right without the pruning having happened.
        var aggregated = await Presence.GetAggregatedStatusAsync(userId, ct);
        var online     = await Presence.IsUserOnlineAsync(userId, ct);

        Assert.Multiple(() =>
        {
            Assert.That(online, Is.False,
                "the presence key is gone, so by the service's own definition nobody is here");
            Assert.That(aggregated, Is.EqualTo(UserStatus.Offline),
                "and the fold has to agree: a session whose status key lapsed alongside it carries nothing");
        });
    }

    /// <summary>
    /// A status write for a pruned session re-adds an index entry that carries nothing, and the next
    /// read prunes it straight back out.
    /// </summary>
    /// <remarks>
    /// <para><c>presence:user:{u}:sessions</c> is a lazily-pruned superset, not a mirror of the
    /// presence keys. <c>SetSessionStatusAsync</c> and <c>HeartbeatAsync</c> <c>SADD</c> their sid
    /// unconditionally, which is what the index was given so that it self-heals for sessions that
    /// predate a deploy or lost their <c>SADD</c>; the index replaced a keyspace <c>SCAN</c>, and a
    /// conditional write would leave it strictly worse than the scan it replaced.</para>
    ///
    /// <para>So the invariant worth guarding is not "the index mirrors the presence keys" — it never
    /// has — but that a re-added dead sid is inert: it does not make the user online, the next
    /// <c>GetActiveSessionIdsAsync</c> drops it again, and once its status key has lapsed with it
    /// (they share a TTL and a refresher in production) it carries no vote either. (Design question
    /// S20: the mirror assertion was a white-box claim against a structure the service documents as
    /// lazily pruned.)</para>
    /// </remarks>
    [Test, CancelAfter(120_000)]
    public async Task ASetStatusForAPrunedSession_ReAddsAnInertIndexEntry(CancellationToken ct = default)
    {
        var userId = Guid.NewGuid();
        var dead   = $"dead-{Guid.NewGuid():N}";

        await PresenceImpl.SetSessionOnlineAsync(userId, dead, TimeSpan.FromSeconds(30), ct);
        await Presence.SetSessionStatusAsync(userId, dead, UserStatus.Online, ct);

        await LapseAsync(Keys.Session(userId, dead), "the session's presence key to lapse", ct);

        var prunedIds = await Presence.GetActiveSessionIdsAsync(userId, ct);

        // The dead session's status changes anyway: a bot tick, a stale grain, a retried call.
        await Presence.SetSessionStatusAsync(userId, dead, UserStatus.Online, ct);

        // Read the raw set first: the two calls below prune the index as a side effect.
        var indexed     = await Cache.SetMembersAsync(Keys.Sessions(userId), ct);
        var online      = await Presence.IsUserOnlineAsync(userId, ct);
        var prunedAgain = await Presence.GetActiveSessionIdsAsync(userId, ct);

        // And the state production actually reaches: the status key lapsed alongside the presence key.
        await LapseAsync(Keys.SessionStatus(userId, dead), "the session's status key to lapse", ct);
        await Presence.RemoveSessionStatusAsync(userId, $"never-existed-{Guid.NewGuid():N}", ct);

        var aggregated = await Presence.GetAggregatedStatusAsync(userId, ct);

        Assert.Multiple(() =>
        {
            Assert.That(prunedIds, Is.Empty, "a session with no presence key is not an active session");
            Assert.That(indexed, Does.Contain(dead),
                "the unconditional SADD is the index's self-heal; a write that skipped it would strand every "
              + "session whose index entry predates a deploy");
            Assert.That(online, Is.False, "but the entry buys the dead session nothing: the user is not online");
            Assert.That(prunedAgain, Is.Empty, "and the next read prunes it straight back out");
            Assert.That(aggregated, Is.EqualTo(UserStatus.Offline),
                "once its status key has lapsed too — which in production happens on the same TTL and the same "
              + "tick — the dead session carries no vote either");
        });
    }

    /// <summary>
    /// A heartbeat does not resurrect a session whose presence key has lapsed; reconnecting does.
    /// </summary>
    /// <remarks>
    /// <para><c>HeartbeatAsync</c> is an <c>EXPIRE</c> and stays one. The lapse of
    /// <c>presence:user:{u}:session:{sid}</c> is the system's "this device is gone" signal, and three
    /// separate mechanisms depend on it: <c>DetachConnectionAsync</c> stops refreshing "so the TTL
    /// can lapse if the device is really gone", the grace reminder finalizes on it, and
    /// <c>SecurityGrain.EndSessionAsync</c> treats it as the backstop for a session whose grain it
    /// could not reach. A keep-alive that recreated the key would put a revoked session back on the
    /// air underneath every one of those checks — and it still could not restore the status key,
    /// because the service does not know the status; only the grain does.</para>
    ///
    /// <para>The designated repair is the reconnect: <c>AppHub.OnConnectedAsync</c> →
    /// <c>UserSessionGrain.AttachConnectionAsync</c> → <c>SetSessionOnlineAsync</c>, which the grain
    /// calls unconditionally so "a brief lapse self-heals", and which the desktop client reaches on
    /// every drop. Under a healthy session the key cannot lapse at all: a 15 s tick against a 120 s
    /// TTL is an eight-fold margin. (Design question S21.)</para>
    /// </remarks>
    [Test, CancelAfter(120_000)]
    public async Task AHeartbeatDoesNotResurrectALapsedSession_ButReconnectingDoes(CancellationToken ct = default)
    {
        var userId = Guid.NewGuid();
        var sid    = $"beat-{Guid.NewGuid():N}";

        await PresenceImpl.SetSessionOnlineAsync(userId, sid, TimeSpan.FromSeconds(30), ct);
        await Presence.SetSessionStatusAsync(userId, sid, UserStatus.Online, ct);

        await LapseAsync(Keys.Session(userId, sid), "the session's presence key to lapse", ct);

        await Presence.HeartbeatAsync(userId, sid, ct);

        var aliveAfterHeartbeat  = await Presence.IsSessionAliveAsync(userId, sid, ct);
        var onlineAfterHeartbeat = await Presence.IsUserOnlineAsync(userId, ct);

        // What the hub does on every connect, and the grain on every attach.
        await PresenceImpl.SetSessionOnlineAsync(userId, sid, SessionTtl, ct);

        var aliveAfterAttach  = await Presence.IsSessionAliveAsync(userId, sid, ct);
        var onlineAfterAttach = await Presence.IsUserOnlineAsync(userId, ct);

        Assert.Multiple(() =>
        {
            Assert.That(aliveAfterHeartbeat, Is.False,
                "the keep-alive recreated a presence key that had lapsed — the very signal the grace and "
              + "revocation paths use to decide a device is gone");
            Assert.That(onlineAfterHeartbeat, Is.False, "so a heartbeat alone does not make the user online");
            Assert.That(aliveAfterAttach, Is.True,
                "and the reconnect, which is the repair the grain performs on every attach, did not restore it");
            Assert.That(onlineAfterAttach, Is.True, "so a reconnected session is online again");
        });
    }

    // ---------------------------------------------------------------------------------------------
    // Session lifecycle and naming
    // ---------------------------------------------------------------------------------------------

    /// <summary>
    /// Removing a session forgets its presence key, its index entry and its naming record.
    /// </summary>
    [Test, CancelAfter(120_000)]
    public async Task RemoveSession_ForgetsTheSessionAndItsNamingRecord(CancellationToken ct = default)
    {
        var userId = Guid.NewGuid();
        var sid    = $"gone-{Guid.NewGuid():N}";

        await PresenceImpl.SetSessionOnlineAsync(userId, sid, SessionTtl, ct);
        await Presence.TouchSessionMetaAsync(userId, sid, "Argon/1.0 (desktop)", "DE", ct);
        await Presence.HeartbeatAsync(userId, sid, ct);

        Assert.That(await Presence.GetSessionMetaAsync(userId, sid, ct), Is.Not.Null,
            "precondition: the session has a naming record");

        await Presence.RemoveSessionAsync(userId, sid, ct);

        var alive   = await Presence.IsSessionAliveAsync(userId, sid, ct);
        var indexed = await Cache.SetMembersAsync(Keys.Sessions(userId), ct);
        var meta    = await Presence.GetSessionMetaAsync(userId, sid, ct);
        var seen    = await Cache.KeyExistsAsync(Keys.Seen(userId, sid), ct);

        Assert.Multiple(() =>
        {
            Assert.That(alive, Is.False, "the presence key is deleted, not left to lapse");
            Assert.That(indexed, Does.Not.Contain(sid), "and the live-session index no longer names it");
            Assert.That(meta, Is.Null, "a removed session has no devices-screen row left behind");
            Assert.That(seen, Is.False, "including its last-seen stamp, or the row comes back anonymous");
        });
    }

    /// <summary>
    /// Ending a session — the documented two-call pair — leaves the user Offline.
    /// </summary>
    /// <remarks>
    /// <para><c>RemoveSessionAsync</c> and <c>RemoveSessionStatusAsync</c> are two halves of one
    /// documented API: the first deletes the presence key, the index entry and the naming record; the
    /// second deletes the status key <em>and recalculates the fold</em>. Every presence-bearing caller
    /// pairs them, status first, and the order is deliberate —
    /// <c>UserSessionGrain.FinalizeOfflineAsync</c> carries a comment explaining why it matters to the
    /// <c>IsUserOnlineAsync</c> that follows, and <c>SecurityGrain.EndSessionAsync</c> does the same.
    /// The pair is what "this session ended" means to the product, so the pair is what is pinned.</para>
    ///
    /// <para>Called alone, <c>RemoveSessionAsync</c> leaves the cached aggregate naming a status for a
    /// user with no sessions — bounded by the 120 s TTL on both the status and the aggregate key, with
    /// nothing left to refresh either, since the grain's tick is the only refresher and finalizing
    /// disposes it. Whether the single call should also recalculate is an API-ergonomics question
    /// (S20), not a reachable defect: the one unpaired call site is <c>AccountDeletionGrain</c>, on an
    /// account being anonymized anyway.</para>
    /// </remarks>
    [Test, CancelAfter(120_000)]
    public async Task RemovingASessionAndItsStatus_LeavesTheUserOffline(CancellationToken ct = default)
    {
        var userId = Guid.NewGuid();
        var sid    = $"last-{Guid.NewGuid():N}";

        await PresenceImpl.SetSessionOnlineAsync(userId, sid, SessionTtl, ct);
        await Presence.SetSessionStatusAsync(userId, sid, UserStatus.Online, ct);

        Assert.That(await Presence.GetAggregatedStatusAsync(userId, ct), Is.EqualTo(UserStatus.Online),
            "precondition: the user's only session is online");

        // The order FinalizeOfflineAsync and EndSessionAsync both use.
        await Presence.RemoveSessionStatusAsync(userId, sid, ct);
        await Presence.RemoveSessionAsync(userId, sid, ct);

        var aggregated = await Presence.GetAggregatedStatusAsync(userId, ct);
        var online     = await Presence.IsUserOnlineAsync(userId, ct);

        Assert.Multiple(() =>
        {
            Assert.That(online, Is.False, "the session is gone");
            Assert.That(aggregated, Is.EqualTo(UserStatus.Offline),
                "a user whose only session was ended is Offline; the cached aggregate must not survive the call "
              + "that recalculates it");
        });
    }

    /// <summary>
    /// A session is described once, and only its last-seen stamp moves afterwards.
    /// </summary>
    /// <remarks>
    /// A reconnecting client asks for a new ticket every time and describes itself identically; if
    /// the second description overwrote the first, <c>StartedAt</c> would silently become "when the
    /// connection last flapped" and the devices screen would say a week-old session started a minute
    /// ago. The last-seen half is the opposite: it exists to move, and it is the only thing on that
    /// screen that distinguishes a session that is alive from one that is merely inside its TTL.
    /// </remarks>
    [Test, CancelAfter(120_000)]
    public async Task ASessionIsNamedOnce_AndItsLastSeenMovesWithHeartbeats(CancellationToken ct = default)
    {
        var userId  = Guid.NewGuid();
        var sid     = $"meta-{Guid.NewGuid():N}";
        var started = DateTime.UtcNow.AddMinutes(-5);

        var created = await Presence.TouchSessionMetaAsync(userId, sid,
            new UserSessionMeta("Argon/1.0 (desktop)", "DE", started, started), ct);

        var again = await Presence.TouchSessionMetaAsync(userId, sid,
            new UserSessionMeta("Argon/9.9 (phone)", "FR", DateTime.UtcNow, DateTime.UtcNow), ct);

        var afterSecondTouch = await Presence.GetSessionMetaAsync(userId, sid, ct);

        await Presence.HeartbeatAsync(userId, sid, ct);
        var afterHeartbeat = await Presence.GetSessionMetaAsync(userId, sid, ct);

        // A second heartbeat has to move it again; poll rather than sleep, because the only thing
        // being waited on is the clock ticking past the resolution of the previous stamp.
        var beat = afterHeartbeat!.LastSeenAt;
        UserSessionMeta? afterSecondHeartbeat = null;
        await WaitUntilAsync(async () =>
        {
            await Presence.HeartbeatAsync(userId, sid, ct);
            afterSecondHeartbeat = await Presence.GetSessionMetaAsync(userId, sid, ct);
            return afterSecondHeartbeat!.LastSeenAt > beat;
        }, "a later heartbeat to move the last-seen stamp", ct);

        Assert.Multiple(() =>
        {
            Assert.That(created, Is.True, "the first description of a session creates the record");
            Assert.That(again, Is.False, "a second description of the same session does not");
            Assert.That(afterSecondTouch!.ClientName, Is.EqualTo("Argon/1.0 (desktop)"),
                "the first description is kept, not overwritten by the reconnect's");
            Assert.That(afterSecondTouch.Region, Is.EqualTo("DE"));
            Assert.That(afterSecondTouch.StartedAt, Is.EqualTo(started).Within(TimeSpan.FromSeconds(1)),
                "so the session still started when it started");
            Assert.That(afterHeartbeat!.LastSeenAt, Is.GreaterThan(started.AddMinutes(4)),
                "a heartbeat moves the last-seen stamp to now — a devices screen that froze it at the connect "
              + "time cannot tell a live session from one inside its TTL");
            Assert.That(afterSecondHeartbeat!.LastSeenAt, Is.GreaterThan(beat),
                "and it keeps moving with every heartbeat");
        });
    }

    /// <summary>
    /// A session that only ever heartbeats is described as anonymous rather than dropped.
    /// </summary>
    /// <remarks>
    /// Bot sessions never pass through the ticket exchange, so they have no naming record at all.
    /// Returning null for them would take them off the devices screen — the one place a user goes to
    /// end a session they do not recognise, which is precisely the session they cannot name.
    /// </remarks>
    [Test, CancelAfter(120_000)]
    public async Task AHeartbeatOnlySession_ReadsAsAnonymousRatherThanMissing(CancellationToken ct = default)
    {
        var userId = Guid.NewGuid();
        var sid    = $"anon-{Guid.NewGuid():N}";
        var before = DateTime.UtcNow.AddSeconds(-1);

        await Presence.HeartbeatAsync(userId, sid, ct);

        var meta = await Presence.GetSessionMetaAsync(userId, sid, ct);

        Assert.Multiple(() =>
        {
            Assert.That(meta, Is.Not.Null, "a heartbeat alone still describes a session the user can end");
            Assert.That(meta!.ClientName, Is.Empty, "with no name, rather than an invented one");
            Assert.That(meta.Region, Is.Empty);
            Assert.That(meta.LastSeenAt, Is.GreaterThanOrEqualTo(before), "and a truthful last-seen");
            Assert.That(meta.StartedAt, Is.EqualTo(meta.LastSeenAt),
                "nothing knows when it started, so the last thing heard from it is the honest answer");
        });

        // The unknown session of an unknown user is simply absent — not an anonymous row.
        Assert.That(await Presence.GetSessionMetaAsync(Guid.NewGuid(), sid, ct), Is.Null);
    }

    /// <summary>
    /// The last-seen stamp is read back in both shapes it has ever been written in.
    /// </summary>
    /// <remarks>
    /// It used to be stamped as raw ticks and refreshed as a round-trip ("O") string while only the
    /// ticks were parsed, which froze every session's last-seen at its connect time. Both shapes are
    /// parsed now, and a deploy that only half-rewrites them must not reintroduce the freeze — so
    /// both are pinned here, against a record written directly rather than through the service.
    /// </remarks>
    [Test, CancelAfter(120_000)]
    public async Task LastSeen_IsParsedFromBothTicksAndRoundTripStamps(CancellationToken ct = default)
    {
        var userId = Guid.NewGuid();
        var sid    = $"seen-{Guid.NewGuid():N}";

        var legacy = new DateTime(DateTime.UtcNow.AddMinutes(-3).Ticks, DateTimeKind.Utc);
        await Cache.StringSetAsync(Keys.Seen(userId, sid), legacy.Ticks.ToString(), TimeSpan.FromMinutes(5), ct);
        var fromTicks = await Presence.GetSessionMetaAsync(userId, sid, ct);

        var modern = new DateTime(DateTime.UtcNow.AddMinutes(-1).Ticks, DateTimeKind.Utc);
        await Cache.StringSetAsync(Keys.Seen(userId, sid), modern.ToString("O"), TimeSpan.FromMinutes(5), ct);
        var fromRoundTrip = await Presence.GetSessionMetaAsync(userId, sid, ct);

        // A named session's stamp comes from the heartbeat, not from the description it was written
        // with — that is the half the freeze bug got wrong, and the description carries a LastSeenAt
        // of its own that would look plausible for ever.
        var started = DateTime.UtcNow.AddMinutes(-3);
        await Presence.TouchSessionMetaAsync(userId, sid,
            new UserSessionMeta("Argon/1.0 (desktop)", "DE", started, started), ct);
        await Presence.HeartbeatAsync(userId, sid, ct);
        var named = await Presence.GetSessionMetaAsync(userId, sid, ct);

        Assert.Multiple(() =>
        {
            Assert.That(fromTicks!.LastSeenAt, Is.EqualTo(legacy).Within(TimeSpan.FromMilliseconds(2)),
                "a stamp written as ticks by an older build still reads");
            Assert.That(fromRoundTrip!.LastSeenAt, Is.EqualTo(modern).Within(TimeSpan.FromMilliseconds(2)),
                "and so does the round-trip shape written today");
            Assert.That(named!.ClientName, Is.EqualTo("Argon/1.0 (desktop)"));
            Assert.That(named.StartedAt, Is.EqualTo(started).Within(TimeSpan.FromSeconds(1)));
            Assert.That(named.LastSeenAt, Is.GreaterThan(started.AddMinutes(2)),
                "the heartbeat's stamp wins over the one baked into the description — the devices screen would "
              + "otherwise show every session as last seen when it connected");
        });
    }

    // ---------------------------------------------------------------------------------------------
    // Broadcast hysteresis
    // ---------------------------------------------------------------------------------------------

    /// <summary>
    /// The hysteresis record lets a genuinely new status through and swallows the repeats.
    /// </summary>
    [Test, CancelAfter(120_000)]
    public async Task MarkBroadcastIfChanged_LetsThroughOnlyRealChanges(CancellationToken ct = default)
    {
        var userId = Guid.NewGuid();

        var first  = await Presence.MarkBroadcastIfChangedAsync(userId, UserStatus.Online, ct);
        var repeat = await Presence.MarkBroadcastIfChangedAsync(userId, UserStatus.Online, ct);
        var change = await Presence.MarkBroadcastIfChangedAsync(userId, UserStatus.DoNotDisturb, ct);
        var again  = await Presence.MarkBroadcastIfChangedAsync(userId, UserStatus.DoNotDisturb, ct);
        var back   = await Presence.MarkBroadcastIfChangedAsync(userId, UserStatus.Online, ct);

        var ttl = await TtlOfAsync(Keys.LastBroadcast(userId));

        Assert.Multiple(() =>
        {
            Assert.That(first, Is.True, "nothing recorded yet is a change");
            Assert.That(repeat, Is.False, "a reconnect that nets the same status must not fan out again");
            Assert.That(change, Is.True);
            Assert.That(again, Is.False);
            Assert.That(back, Is.True, "returning to a previous status is still a change");
            Assert.That(ttl, Is.Not.Null.And.GreaterThan(TimeSpan.FromMinutes(29)),
                "the record has to outlive a session TTL or it stops suppressing anything");
        });
    }

    /// <summary>
    /// Twenty callers announcing the same status produce exactly one broadcast.
    /// </summary>
    /// <remarks>
    /// <para>This is the whole job of the record: <c>UserGrain</c> is a <c>[StatelessWorker]</c>, so
    /// several activations of one user fan out concurrently and each asks the record whether its
    /// status is new. As a read followed by an unconditional write, with nothing atomic between them,
    /// every caller that reads before the first write reads "nothing recorded" and returns true — so
    /// n concurrent reconnects of one user cost n fan-outs to every space and every friend, which is
    /// the storm the record exists to prevent.</para>
    ///
    /// <para>The test spreads the burst over twenty independent users so the verdict does not hang on
    /// one lucky interleaving: a single racing user is chance, twenty all serialising perfectly is
    /// not.</para>
    ///
    /// <para>The contract it guards (defect S19, fixed): the record is written and read in one
    /// operation — <c>IArgonCacheDatabase.StringSetAndGetPreviousAsync</c>, Redis
    /// <c>SET … EX … GET</c> — so the answer comes from the write that actually happened rather than
    /// from a read that every racing caller made before any of them wrote. Exactly one of N callers
    /// announcing the same status sees the transition, whatever the interleaving.</para>
    /// </remarks>
    [Test, CancelAfter(120_000)]
    public async Task MarkBroadcastIfChanged_UnderConcurrency_AnnouncesOnce(CancellationToken ct = default)
    {
        const int users   = 20;
        const int callers = 20;

        var trues = new List<int>();

        for (var u = 0; u < users; u++)
        {
            var userId  = Guid.NewGuid();
            var results = await Task.WhenAll(Enumerable
               .Range(0, callers)
               .Select(_ => Presence.MarkBroadcastIfChangedAsync(userId, UserStatus.DoNotDisturb, ct))
               .ToArray());

            trues.Add(results.Count(x => x));
        }

        Assert.That(trues.Sum(), Is.EqualTo(users),
            $"{callers} concurrent callers announcing one status for one user must yield one broadcast each time; "
          + $"got {trues.Sum()} broadcasts across {users} users ({string.Join(", ", trues)})");
    }

    // ---------------------------------------------------------------------------------------------
    // Activity presence
    // ---------------------------------------------------------------------------------------------

    private static UserActivityPresence Activity(string title, ActivityPresenceKind kind, ulong startedAtSeconds)
        => new(kind, startedAtSeconds, title);

    /// <summary>
    /// Two devices carry two activities, and the one the single-activity wire shows is the newest.
    /// </summary>
    [Test, CancelAfter(120_000)]
    public async Task ActivityIsPerSession_AndTheRepresentativeIsTheMostRecentlyStarted(CancellationToken ct = default)
    {
        var userId = Guid.NewGuid();
        var deskId = $"desk-{Guid.NewGuid():N}";
        var phone  = $"phone-{Guid.NewGuid():N}";

        await PresenceImpl.SetSessionOnlineAsync(userId, deskId, SessionTtl, ct);
        await PresenceImpl.SetSessionOnlineAsync(userId, phone, SessionTtl, ct);

        var now = (ulong)DateTimeOffset.UtcNow.ToUnixTimeSeconds();

        await Presence.BroadcastActivityPresence(Activity("Deep Rock Galactic", ActivityPresenceKind.GAME, now - 600), userId, deskId);
        await Presence.BroadcastActivityPresence(Activity("Rock and Stone", ActivityPresenceKind.LISTEN, now - 30), userId, phone);

        var activities = await Presence.GetUserActivitiesAsync(userId);
        var represents = await Presence.GetUsersActivityPresence(userId);
        var batched    = await Presence.BatchGetUsersActivityPresence([userId, Guid.NewGuid()]);

        Assert.Multiple(() =>
        {
            Assert.That(activities.Select(a => a.titleName),
                Is.EquivalentTo(new[] { "Deep Rock Galactic", "Rock and Stone" }),
                "one device's activity must not clobber the other's");
            Assert.That(represents!.titleName, Is.EqualTo("Rock and Stone"),
                "the single-activity wire shows the most recently started one");
            Assert.That(batched.Keys, Is.EquivalentTo(new[] { userId }),
                "a user with no activity is absent from the batch rather than present with a null");
            Assert.That(batched[userId].titleName, Is.EqualTo("Rock and Stone"));
        });
    }

    /// <summary>
    /// Removing an activity says whether there was one, so the caller knows to announce the removal.
    /// </summary>
    [Test, CancelAfter(120_000)]
    public async Task RemovingAnActivity_ReportsWhetherThereWasOne(CancellationToken ct = default)
    {
        var userId = Guid.NewGuid();
        var sid    = $"act-{Guid.NewGuid():N}";

        await PresenceImpl.SetSessionOnlineAsync(userId, sid, SessionTtl, ct);
        await Presence.BroadcastActivityPresence(
            Activity("Factorio", ActivityPresenceKind.GAME, (ulong)DateTimeOffset.UtcNow.ToUnixTimeSeconds()), userId, sid);

        var removedReal    = await Presence.RemoveActivityPresence(userId, sid);
        var removedAgain   = await Presence.RemoveActivityPresence(userId, sid);
        var removedUnknown = await Presence.RemoveActivityPresence(Guid.NewGuid(), sid);
        var left           = await Presence.GetUserActivitiesAsync(userId);

        Assert.Multiple(() =>
        {
            Assert.That(removedReal, Is.True, "there was one, so observers have to be told it is gone");
            Assert.That(removedAgain, Is.False, "there is not any more, so a second removal announces nothing");
            Assert.That(removedUnknown, Is.False);
            Assert.That(left, Is.Empty);
        });
    }

    /// <summary>
    /// An activity written for a session outside the live index is invisible to readers, and still
    /// clearable.
    /// </summary>
    /// <remarks>
    /// <para><c>GetUserActivitiesAsync</c> is documented as every <em>live</em> session's activity and
    /// folds <c>presence:user:{u}:sessions</c>, so a sid that is not live contributes nothing — the
    /// same rule that stops an Offline member being rendered mid-game.
    /// <c>RemoveActivityPresence</c>, by contrast, addresses the key directly and never consults the
    /// index, which is what makes such a write clearable rather than a leak:
    /// <c>UserGrain.RemoveBroadcastPresenceAsync</c> with <c>alwaysBroadcast: true</c> retracts it from
    /// every space even when the key has already gone.</para>
    ///
    /// <para>The asymmetry is deliberate rather than a hole (design question S17). The write is
    /// accepted so that a client announcing a game before its socket has attached — app boot with a
    /// game already running, or an announce landing in a reconnect gap — is not silently dropped,
    /// because the desktop client dedupes on <c>lastPublishedPresence</c> and never re-sends. The
    /// stream leads, the snapshot catches up the moment the session attaches, and the two converge;
    /// that convergence is pinned end to end in
    /// <c>PresenceActivityTests.An_activity_announced_before_the_hub_attaches_reaches_the_room_then_the_snapshot</c>.
    /// </para>
    /// </remarks>
    [Test, CancelAfter(120_000)]
    public async Task AnActivityForASessionOutsideTheIndex_IsInvisibleToReadersAndStillClearable(
        CancellationToken ct = default)
    {
        var userId  = Guid.NewGuid();
        var unknown = $"unindexed-{Guid.NewGuid():N}";

        await Presence.BroadcastActivityPresence(
            Activity("Hollow Knight", ActivityPresenceKind.GAME, (ulong)DateTimeOffset.UtcNow.ToUnixTimeSeconds()),
            userId, unknown);

        var activities = await Presence.GetUserActivitiesAsync(userId);
        var represents = await Presence.GetUsersActivityPresence(userId);
        var stored     = await Cache.StringGetAsync(Keys.Activity(userId, unknown), ct);
        var removed    = await Presence.RemoveActivityPresence(userId, unknown);
        var afterwards = await Cache.StringGetAsync(Keys.Activity(userId, unknown), ct);

        Assert.Multiple(() =>
        {
            Assert.That(stored, Is.Not.Null.And.Not.Empty, "the write did land in Redis");
            Assert.That(activities, Is.Empty,
                "a session that is not in the live index is not playing anything as far as readers are concerned");
            Assert.That(represents, Is.Null, "and there is no representative activity to hand a space snapshot");
            Assert.That(removed, Is.True,
                "the clear path addresses the key directly, so the announcing user can always retract it");
            Assert.That(afterwards, Is.Null.Or.Empty, "and the key is gone once they have");
        });
    }

    /// <summary>
    /// An activity outlives the ten minutes it starts with, as long as its session keeps announcing itself.
    /// </summary>
    /// <remarks>
    /// <para>H6. <c>activity:…:session:{sid}</c> is written with a 10 min TTL and nothing refreshes
    /// it: not the session tick, not the heartbeat, not a status change, and not the client, which
    /// dedupes identical presence and so never re-sends. Ten minutes into a game the key lapses; a
    /// member joining the space afterwards sees no activity while everyone already watching keeps the
    /// stale one for ever, because expiry produces no removal event.</para>
    ///
    /// <para>The TTL is drained deliberately here instead of waiting ten minutes: the question is
    /// whether any of the calls a live session makes puts it back, and that answer does not depend
    /// on how the key got close to expiry.</para>
    ///
    /// <para>The contract it guards (defect S16, fixed): the session keep-alive owns the activity's
    /// lifetime. <c>RefreshSessionStatusTtlAsync</c> — the call the session grain's 15 s tick and the
    /// bot gateway's both make — now re-arms <c>activity:user:{u}:session:{sid}</c> alongside the
    /// status keys, so ten minutes stopped being how long a game may last and became how long an
    /// orphaned entry lingers after its session stopped ticking. It is an <c>EXPIRE</c>, so a
    /// cleared activity is never resurrected by a later tick.</para>
    /// </remarks>
    [Test, CancelAfter(120_000)]
    public async Task AnActivityIsRefreshedByTheSessionThatKeepsAnnouncingIt(CancellationToken ct = default)
    {
        var userId = Guid.NewGuid();
        var sid    = $"game-{Guid.NewGuid():N}";
        var key    = Keys.Activity(userId, sid);

        await PresenceImpl.SetSessionOnlineAsync(userId, sid, SessionTtl, ct);
        await Presence.SetSessionStatusAsync(userId, sid, UserStatus.InGame, ct);
        await Presence.BroadcastActivityPresence(
            Activity("Deep Rock Galactic", ActivityPresenceKind.GAME, (ulong)DateTimeOffset.UtcNow.ToUnixTimeSeconds()),
            userId, sid);

        var fresh = await TtlOfAsync(key);

        // Stand where ten minutes of play would leave it.
        await Cache.UpdateStringExpirationAsync(key, TimeSpan.FromSeconds(30), ct);

        // Everything a live session does over the following seconds.
        await Presence.HeartbeatAsync(userId, sid, ct);
        await Presence.RefreshSessionStatusTtlAsync(userId, sid, ct);
        await Presence.SetSessionStatusAsync(userId, sid, UserStatus.InGame, ct);
        await PresenceImpl.SetSessionOnlineAsync(userId, sid, SessionTtl, ct);

        var afterKeepAlive = await TtlOfAsync(key);

        Assert.Multiple(() =>
        {
            Assert.That(fresh, Is.Not.Null.And.GreaterThan(TimeSpan.FromMinutes(9)),
                "a new activity starts with the documented ten minutes");
            Assert.That(afterKeepAlive, Is.Not.Null.And.GreaterThan(TimeSpan.FromMinutes(9)),
                "and a session that is still alive and still in the same game must carry its activity with it — "
              + "an activity that expires under a running game shows the user as doing nothing");
        });
    }

    // ---------------------------------------------------------------------------------------------
    // Batch reads
    // ---------------------------------------------------------------------------------------------

    /// <summary>
    /// Users nobody has ever heard of read as offline, once each, without an exception.
    /// </summary>
    /// <remarks>
    /// The batch reads are what a space roster and a friends list are built from, and both are asked
    /// about ids that have no presence at all: members who have never connected, friends who are
    /// offline. Missing keys have to answer Offline rather than throwing or dropping the id, or a
    /// roster silently loses rows.
    /// </remarks>
    [Test, CancelAfter(120_000)]
    public async Task UnknownUsers_ReadAsOfflineInEveryBatchApi(CancellationToken ct = default)
    {
        var strangerA = Guid.NewGuid();
        var strangerB = Guid.NewGuid();
        var known     = Guid.NewGuid();
        var sid       = $"known-{Guid.NewGuid():N}";

        await PresenceImpl.SetSessionOnlineAsync(known, sid, SessionTtl, ct);
        await Presence.SetSessionStatusAsync(known, sid, UserStatus.Online, ct);

        List<Guid> asked = [strangerA, strangerB, known, known];

        var online     = await Presence.AreUsersOnlineAsync(asked, ct);
        var aggregated = await Presence.BatchGetAggregatedStatusAsync(asked, ct);
        var activities = await Presence.BatchGetUsersActivityPresence(asked);
        var single     = await Presence.GetAggregatedStatusAsync(strangerA, ct);
        var emptyOnline = await Presence.AreUsersOnlineAsync([], ct);
        var emptyStatus = await Presence.BatchGetAggregatedStatusAsync([], ct);

        Assert.Multiple(() =>
        {
            Assert.That(online.Keys, Is.EquivalentTo(new[] { strangerA, strangerB, known }),
                "every id asked about comes back exactly once, duplicates folded");
            Assert.That(online[strangerA], Is.False);
            Assert.That(online[strangerB], Is.False);
            Assert.That(online[known], Is.True);

            Assert.That(aggregated.Keys, Is.EquivalentTo(new[] { strangerA, strangerB, known }));
            Assert.That(aggregated[strangerA], Is.EqualTo(UserStatus.Offline), "no key means Offline, not an exception");
            Assert.That(aggregated[strangerB], Is.EqualTo(UserStatus.Offline));
            Assert.That(aggregated[known], Is.EqualTo(UserStatus.Online),
                "and the batch read agrees with the single read for the same user");

            Assert.That(single, Is.EqualTo(UserStatus.Offline));
            Assert.That(activities, Is.Empty, "nobody is doing anything");
            Assert.That(emptyOnline, Is.Empty, "asking about nobody answers nothing rather than throwing");
            Assert.That(emptyStatus, Is.Empty);
        });
    }

    // ---------------------------------------------------------------------------------------------
    // The device-switch race
    // ---------------------------------------------------------------------------------------------

    /// <summary>
    /// Switching device never leaves a connected user Offline (H7, at the layer the race lives in).
    /// </summary>
    /// <remarks>
    /// <para><c>RecalculateAggregatedStatusAsync</c> is read-fold-write with nothing atomic about it,
    /// and its two callers race whenever a user picks up a second device as the first one goes: the
    /// new session's <c>SetSessionStatusAsync</c> adds itself to the index and folds, the old
    /// session's <c>RemoveSessionStatusAsync</c> folds too, and if the removal's fold ran before the
    /// arrival's index write but its own write lands last, the user is left cached as Offline while a
    /// device is sitting there connected — and every observer is told so, because the grain above
    /// broadcasts what the fold produced.</para>
    ///
    /// <para>Each iteration is a fresh user so no other session can mask the result; the assertion
    /// message carries the failure count, because a race that fires a few times in fifty is exactly
    /// as broken as one that fires every time and much easier to dismiss.</para>
    /// </remarks>
    [Test, CancelAfter(120_000)]
    public async Task ADeviceSwitch_NeverLeavesAConnectedUserOffline(CancellationToken ct = default)
    {
        const int iterations = 50;

        // Sids whose presence keys lapsed stay in the live-session index (see
        // ASetStatusForAPrunedSession_DoesNotResurrectIt), so a real user's fold walks far more
        // members than they have devices. Padding the index gives the two concurrent folds enough
        // overlap that the order their writes land in is decided by chance rather than by the two or
        // three operations that separate their starts — which is what a grain hop supplies in production.
        const int staleInIndex = 40;

        var failures = new List<string>();

        for (var i = 0; i < iterations; i++)
        {
            var userId = Guid.NewGuid();
            var oldSid = $"old-{i}-{Guid.NewGuid():N}";
            var newSid = $"new-{i}-{Guid.NewGuid():N}";

            for (var pad = 0; pad < staleInIndex; pad++)
                await Cache.SetAddAsync(Keys.Sessions(userId), $"stale-{pad}", ct);

            await PresenceImpl.SetSessionOnlineAsync(userId, oldSid, TimeSpan.FromSeconds(30), ct);
            await Presence.SetSessionStatusAsync(userId, oldSid, UserStatus.Online, ct);

            // The old device's session ends at the same moment the new device's starts. The new
            // session's presence write is inside the race on purpose: it is what puts the sid in the
            // index the other side is folding over, and pre-seeding it would hide the interleaving.
            var leaving = Presence.RemoveSessionStatusAsync(userId, oldSid, ct);
            var arriving = Task.Run(async () =>
            {
                await PresenceImpl.SetSessionOnlineAsync(userId, newSid, TimeSpan.FromSeconds(30), ct);
                await Presence.SetSessionStatusAsync(userId, newSid, UserStatus.Online, ct);
            }, ct);

            await Task.WhenAll(leaving, arriving);

            var aggregated = await Presence.GetAggregatedStatusAsync(userId, ct);
            if (aggregated != UserStatus.Online)
                failures.Add($"#{i}: {aggregated}");
        }

        Assert.That(failures, Is.Empty,
            $"{failures.Count} of {iterations} device switches ended with a connected user reading something other "
          + $"than Online: {string.Join(", ", failures)}");
    }
}
