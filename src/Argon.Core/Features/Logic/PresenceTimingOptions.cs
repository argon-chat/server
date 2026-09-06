namespace Argon.Features.Logic;

using Argon.Features.Clustering;

/// <summary>
/// Every clock presence runs on, in one place.
/// </summary>
/// <remarks>
/// <para>These were nine <c>static readonly TimeSpan</c>s spread over <c>UserPresenceService</c> and
/// <c>UserSessionGrain</c>, and they are a system rather than nine independent numbers: the refresh
/// tick has to fit several times inside the session TTL or a healthy session lapses between ticks,
/// the grace has to outlast the TTL or a reconnecting client is finalized offline while it is coming
/// back, and the heartbeat debounce has to be shorter than the TTL or a heartbeat is not a
/// keep-alive. Written down as constants those relationships were invisible and unenforced; written
/// down here <see cref="Validate"/> is what enforces them.</para>
///
/// <para>Making them configuration is not about tuning production — the defaults below are exactly
/// the values that shipped, to the second, and nothing in a deployment is expected to change them.
/// It is about the integration suite, which asserts what happens when a TTL lapses, when a grace
/// fires, when a tick does or does not renew a key. Against production timings those assertions cost
/// minutes of pure waiting per test; against the same code with a twelve-second TTL they cost
/// seconds and assert exactly the same thing, because every one of them is written as a ratio of
/// these values rather than as a wall-clock number.</para>
/// </remarks>
public sealed class PresenceTimingOptions : IValidatableFeatureOptions
{
    public const string SectionName = "Presence";

    /// <summary>
    /// How long <c>presence:user:{u}:session:{sid}</c> and the status keys live with nothing renewing
    /// them — i.e. how long after its last tick a session is still believed to be alive.
    /// </summary>
    /// <remarks>
    /// The grace period a dropped client gets in practice: the reminder finalizes it offline only
    /// once this has lapsed, so this is what a laptop lid, a tunnel or a Wi-Fi handover is allowed to
    /// cost before the user's friends are told they left.
    /// </remarks>
    public TimeSpan SessionTtl { get; set; } = TimeSpan.FromSeconds(120);

    /// <summary>The session grain's tick: how often a live session pushes its TTLs back to full.</summary>
    /// <remarks>
    /// Also the resolution of every "is this key still being refreshed" test — a TTL below
    /// <c>SessionTtl - RefreshPeriod</c> means at least one tick was missed, which is the only
    /// evidence there is that a session stopped renewing while its client is still connected.
    /// </remarks>
    public TimeSpan RefreshPeriod { get; set; } = TimeSpan.FromSeconds(15);

    /// <summary>
    /// The shortest interval at which a client heartbeat is allowed to reach Redis.
    /// </summary>
    /// <remarks>
    /// A heartbeat is cheap for a client to send and not free for the server to honour, and the
    /// session's own tick already renews everything on <see cref="RefreshPeriod"/>. Beats arriving
    /// faster than this only re-assert what the tick just wrote, so they are dropped at the grain —
    /// a status <em>change</em> carried by one of them is applied regardless.
    /// </remarks>
    public TimeSpan HeartbeatDebounce { get; set; } = TimeSpan.FromSeconds(30);

    /// <summary>
    /// The period of the durable <c>presence-grace</c> reminder armed when the last connection drops.
    /// </summary>
    /// <remarks>
    /// A reminder rather than a timer because it has to survive the grain deactivating, and a period
    /// rather than a due time because the first tick may land while the presence key is still alive —
    /// in which case it waits for the next one. So the offline a client actually experiences is
    /// <see cref="SessionTtl"/> plus up to one of these, not this on its own.
    /// </remarks>
    public TimeSpan GracePeriod { get; set; } = TimeSpan.FromMinutes(1);

    /// <summary>
    /// Orleans' own floor on a reminder period, which this option <em>sets</em>.
    /// </summary>
    /// <remarks>
    /// <para><c>ReminderOptions.MinimumReminderPeriod</c> is one minute out of the box and
    /// <c>RegisterOrUpdateReminder</c> throws below it, so it silently sets the floor on
    /// <see cref="GracePeriod"/> — which is exactly the sort of coupling that is discovered at
    /// runtime, from a grain, on a disconnect. It is written down instead, and
    /// <see cref="Validate"/> refuses a grace that Orleans would reject.</para>
    ///
    /// <para><b>Written down is not enough, and this used to be only written down.</b> Nothing in
    /// the production host read it, so an operator who lowered the grace and this together — exactly
    /// what the paragraph above invites — passed <c>--validate-config</c> and then met Orleans' real
    /// one-minute floor on the first disconnect: an <c>ArgumentException</c> out of
    /// <c>BeginGraceAsync</c>, out of <c>OnDisconnectedAsync</c>, and no session on that silo
    /// finalized offline again. <c>PresenceFeature.Configure</c> now applies this to
    /// <c>ReminderOptions.MinimumReminderPeriod</c>, so the rule below describes the value Orleans
    /// will actually enforce rather than a number that happened to match it.</para>
    ///
    /// <para>The suite also uses it to size its waits: a grace is not late until the grace period
    /// plus one reminder tick has passed.</para>
    /// </remarks>
    public TimeSpan ReminderFloor { get; set; } = TimeSpan.FromMinutes(1);

    /// <summary>How long a session grain is pinned in memory after any sign of life.</summary>
    /// <remarks>
    /// The connection set lives in the activation, so collecting a session that still has connections
    /// would lose them; this keeps the activation alive comfortably longer than the tick that renews
    /// it. A session with nothing attached stops renewing and is collected normally.
    /// </remarks>
    public TimeSpan DeactivationDelay { get; set; } = TimeSpan.FromMinutes(2);

    /// <summary>
    /// How long a connected session may stay statusless before Online is assumed for it.
    /// </summary>
    /// <remarks>
    /// A hub attach carries no status, and answering that with an immediate Online is what announced
    /// Do-Not-Disturb users as available on every reconnect. Short, because the window it opens is a
    /// connected user who is in no roster at all.
    /// </remarks>
    public TimeSpan StatusDeadline { get; set; } = TimeSpan.FromSeconds(5);

    /// <summary>How long a session's activity entry survives with nothing renewing it.</summary>
    /// <remarks>
    /// A safety net rather than the activity's lifetime — the session tick renews it, so this only
    /// decides how long an orphaned entry lingers after the session announcing it died without
    /// finalizing. Generous on purpose, and deliberately longer than <see cref="SessionTtl"/>.
    /// </remarks>
    public TimeSpan ActivityTtl { get; set; } = TimeSpan.FromMinutes(10);

    /// <summary>
    /// How long after its last announcement an activity is still renewed by the session tick.
    /// </summary>
    /// <remarks>
    /// <para>The bound on <see cref="ActivityTtl"/> being renewed for the life of the session. Making
    /// the renewal unconditional cured an activity that lapsed under a game still being played, and
    /// created its mirror image: the only thing that erases an activity is the client's explicit
    /// <c>RemoveBroadcastPresence</c>, and a client that is killed outright — or whose removal is lost
    /// to a token refresh or a dropped connection — never sends it. The tick then renewed "playing
    /// Portal 2" for the rest of the session, hours after the game closed, where before it lapsed on
    /// its own ten minutes.</para>
    ///
    /// <para>So the renewal is a lease rather than a promise: the client re-announces a live activity
    /// every ~5 minutes, and the tick keeps renewing only while the last announcement is inside this
    /// window. A client that is still there re-arms it three times over before it could close; a
    /// client that died stops re-announcing, the window closes and the entry lapses exactly as it did
    /// before. Longer than <see cref="ActivityTtl"/> by construction — a window inside the lifetime it
    /// bounds would end before the entry it is bounding could expire, which is no bound at all.</para>
    /// </remarks>
    public TimeSpan ActivityReassertWindow { get; set; } = TimeSpan.FromMinutes(15);

    /// <summary>
    /// How long a transport connection may go unheard from before the session stops counting it.
    /// </summary>
    /// <remarks>
    /// <para><b>Three minutes, and the number comes from the browser rather than from us.</b> The
    /// obvious derivation — three times the fifteen-second heartbeat the shipped client sends — is
    /// the wrong one, and reading it that way is how this shipped at a minute. A client's heartbeat
    /// runs on <c>setInterval</c> inside a dedicated worker, and a worker whose page is hidden does
    /// not get a fifteen-second interval: Chromium aligns timers in a hidden page to roughly one wake
    /// per minute after a few minutes backgrounded, and Electron throttles a minimized window the
    /// same way. The client's own <c>useTabLifecycle</c> says as much in its opening paragraph. So
    /// the cadence this has to survive is <em>the throttled one</em>, and the floor is three of those
    /// — three missed wake-ups before a connection stops counting — which is where three minutes
    /// comes from. At the un-throttled cadence that is twelve missed beats, which is generous on
    /// purpose: the cost of waiting is a ghost connection held a little longer, and the cost of not
    /// waiting is a user who left the app open in another tab being announced Offline to every space
    /// and every friend, hung up from any call they were in, while their socket is open.</para>
    ///
    /// <para>It is one of the few intervals here that is <em>not</em> derived from the others, and
    /// deliberately so — it is paced by the client's cadence, which is not one of these options and
    /// does not shrink when a host compresses its clocks. A host that compresses the rest has to
    /// lower this one by hand. It is also not the only thing renewing the lease any more: every hub
    /// method that could only have been called over a live transport stamps it
    /// (<c>AppHub</c> → <c>IUserSessionGrain.MarkConnectionSeenAsync</c>), so a backgrounded client
    /// that is being typed at, resumed or resubscribed renews without waiting for its timer.</para>
    ///
    /// <para><b>Why there is a floor at all.</b> Connection ids are added by an attach and removed by
    /// a detach, and a detach is not guaranteed: <c>AppHub.OnDisconnectedAsync</c> can fault or never
    /// run at all, and nothing else ever removes an id. A connection that outlives its transport is
    /// then self-sustaining — the session's tick keeps renewing the presence key and extending the
    /// activation for as long as the set is non-empty, a per-connection sign-out finds the set still
    /// non-empty and declines to finalize, and the user is Online to everybody until the silo
    /// restarts. The floor turns "attached" from a claim nothing revisits into a lease the client
    /// renews with every heartbeat, which the shipped client sends every fifteen seconds.</para>
    /// </remarks>
    public TimeSpan StaleConnectionAfter { get; set; } = TimeSpan.FromSeconds(180);

    /// <summary>
    /// The shortest interval at which one session re-seeds a connecting client with its friends.
    /// </summary>
    /// <remarks>
    /// <para>The seed belongs to a connection rather than to a session — a transport that came up
    /// after its friends did knows nothing about them — so it runs on every attach. What that leaves
    /// open is a client reconnecting in a loop on one valid ticket: each attach costs a relational
    /// friends query, a session lookup per friend and one <c>forSelf</c> publish per online friend,
    /// and every one of those publishes is an append to the user's own replay stream, which evicts
    /// the events a real reconnect would have wanted.</para>
    ///
    /// <para>So the second and subsequent attaches inside this window are skipped. The case the seed
    /// exists for is not: a session whose connection set went from empty to non-empty is a genuine
    /// reconnect and is seeded regardless of when the last one ran, because that is the client that
    /// has actually missed something.</para>
    /// </remarks>
    public TimeSpan FriendPushDebounce { get; set; } = TimeSpan.FromSeconds(30);

    /// <summary>How long the presence hysteresis record remembers what was last fanned out.</summary>
    /// <remarks>
    /// Kept longer than a session TTL so it bridges the gaps between status changes; if it lapses the
    /// next change simply re-broadcasts, which is harmless, so the only cost of a long value is a
    /// suppressed duplicate that nobody wanted anyway.
    /// </remarks>
    public TimeSpan LastBroadcastTtl { get; set; } = TimeSpan.FromMinutes(30);

    /// <summary>How long the devices-screen record for a session outlives the session itself.</summary>
    /// <remarks>
    /// Deliberately much longer than <see cref="SessionTtl"/>: a session that drops off and heartbeats
    /// back keeps its name instead of reappearing anonymous. Nothing reads it for a session that is
    /// not also in the live index, so a stale one is invisible rather than wrong.
    /// </remarks>
    public TimeSpan SessionMetaTtl { get; set; } = TimeSpan.FromHours(24);

    /// <summary>
    /// The relationships between the values, which is the only reason they are one options class.
    /// </summary>
    /// <remarks>
    /// Each rule below is a way a deployment could look plausible and take every user offline while
    /// they sit connected, so all of them are errors rather than warnings. There is no upper bound on
    /// anything: a long TTL is a product decision, a TTL shorter than the tick that renews it is not.
    /// </remarks>
    public void Validate(IFeatureConfigurationReport report)
    {
        foreach (var (name, value) in new[]
                 {
                     (nameof(SessionTtl), SessionTtl),
                     (nameof(RefreshPeriod), RefreshPeriod),
                     (nameof(HeartbeatDebounce), HeartbeatDebounce),
                     (nameof(GracePeriod), GracePeriod),
                     (nameof(ReminderFloor), ReminderFloor),
                     (nameof(DeactivationDelay), DeactivationDelay),
                     (nameof(StatusDeadline), StatusDeadline),
                     (nameof(ActivityTtl), ActivityTtl),
                     (nameof(ActivityReassertWindow), ActivityReassertWindow),
                     (nameof(StaleConnectionAfter), StaleConnectionAfter),
                     (nameof(FriendPushDebounce), FriendPushDebounce),
                     (nameof(LastBroadcastTtl), LastBroadcastTtl),
                     (nameof(SessionMetaTtl), SessionMetaTtl)
                 })
            report.Require(value > TimeSpan.Zero, name,
                $"is {value}; every presence interval is a duration something waits out, and zero or " +
                "negative means the thing it paces either never happens or happens continuously");

        // Three ticks inside a TTL is the margin that makes a missed tick survivable: one lost to a
        // silo pause or a Redis blip must not be able to take a connected user offline. It is also
        // what makes "the TTL is below SessionTtl - RefreshPeriod" a sound test for "this key stopped
        // being refreshed" rather than for "we read it at an unlucky moment".
        report.Require(RefreshPeriod * 3 < SessionTtl, nameof(RefreshPeriod),
            $"is {RefreshPeriod} against a session TTL of {SessionTtl}; the tick has to fit at least " +
            "three times inside the TTL or one missed tick takes a connected session offline");

        // Below the floor RegisterOrUpdateReminder throws, and it throws inside DetachConnectionAsync
        // — on a disconnect, from a grain, where the only visible symptom is sessions that never
        // finalize offline.
        report.Require(GracePeriod >= ReminderFloor, nameof(GracePeriod),
            $"is {GracePeriod}, below the {ReminderFloor} Orleans reminder floor " +
            $"({nameof(ReminderFloor)}). Orleans rejects the registration, so the grace would never " +
            "be armed and a dropped session would never be finalized offline.");

        // A debounce at or above the TTL turns the heartbeat into a beat that renews nothing: the
        // client's beats are dropped for longer than the key it is meant to be keeping alive lives.
        report.Require(HeartbeatDebounce < SessionTtl, nameof(HeartbeatDebounce),
            $"is {HeartbeatDebounce}, at or above the {SessionTtl} session TTL; heartbeats would be " +
            "debounced for longer than the key they renew survives");

        // The activity is renewed by the same tick as the status keys, so a shorter lifetime means it
        // lapses first and a live session is shown playing nothing.
        report.Require(ActivityTtl >= SessionTtl, nameof(ActivityTtl),
            $"is {ActivityTtl}, below the {SessionTtl} session TTL; an activity would lapse under a " +
            "session that is still announcing it");

        // A window inside the lifetime it bounds bounds nothing: the entry would lapse on its own TTL
        // before the client's next re-announcement was due, which is the defect the renewal cured.
        report.Require(ActivityReassertWindow > ActivityTtl, nameof(ActivityReassertWindow),
            $"is {ActivityReassertWindow}, at or below the {ActivityTtl} activity TTL; the renewal it " +
            "bounds would stop before the entry could expire, so an activity would lapse under a " +
            "client that is still announcing it");

        // The floor is evaluated by the tick, so a floor within a tick or two of it prunes a
        // connection between two of its own heartbeats — the client is there, the server stopped
        // believing it, and the user goes offline mid-session.
        report.Require(StaleConnectionAfter > RefreshPeriod * 2, nameof(StaleConnectionAfter),
            $"is {StaleConnectionAfter} against a {RefreshPeriod} refresh period; the floor is only " +
            "read once per tick, so anything this close to it takes live connections offline for " +
            "missing a single heartbeat");

        // The record has to outlive the session it describes or the devices screen forgets a session
        // that is merely reconnecting.
        report.Require(SessionMetaTtl > SessionTtl, nameof(SessionMetaTtl),
            $"is {SessionMetaTtl}, at or below the {SessionTtl} session TTL; a session that dropped " +
            "and came back would reappear on the devices screen with no name");

        // The activation holds the connection set, so it must not be collected between the ticks that
        // are the only thing renewing it.
        report.Require(DeactivationDelay > RefreshPeriod, nameof(DeactivationDelay),
            $"is {DeactivationDelay}, at or below the {RefreshPeriod} refresh period; the activation " +
            "holding the live connection set would be collected between its own ticks");
    }
}
