namespace Argon.Grains.Interfaces;

/// <summary>
/// One activation per user, and the only thing in the product allowed to decide what that user's
/// presence is or to tell anybody about it.
/// </summary>
/// <remarks>
/// <para><b>Why this grain exists at all: presence is a read-fold-write, and it used to run
/// concurrently with itself.</b> Everything about a user's presence — the aggregate over their
/// sessions, the hysteresis record that suppresses duplicate announcements, the fan-out to their
/// spaces and their friends, and the same three for the activity they are announcing — is a sequence
/// of Redis round trips whose correctness depends on nothing else doing the same sequence at the
/// same instant. All of it lived on <c>UserGrain</c>, which is a <c>[StatelessWorker]</c>: several
/// activations of one user run at once, by design, and that is exactly what a presence fold must not
/// do. It produced two distinct bugs, and both are what this type is for.</para>
///
/// <para><b>The lost update.</b> A device switch — the laptop saying goodbye while the phone says
/// hello — is two folds over one index. The one that read first could write last, so the account was
/// left cached Offline with a live, heartbeating session attached, and nothing recovered from it:
/// later heartbeats carry a status the session already holds, so no fold runs again, and the refresh
/// tick renews the TTL of the wrong value for as long as the user stays connected. This was patched
/// once by moving the fold into a Lua script, which made it atomic at the store — and that had to be
/// undone, because production's cache is Dragonfly and the script reads keys it does not declare:
/// refused by default, and under a global lock when allowed.</para>
///
/// <para><b>The lost order.</b> Even with an atomic fold, two <em>fan-outs</em> for one user can
/// finish in the wrong order: the switch's Offline broadcast and the new device's Online broadcast
/// ran on two activations, and whichever hit the hub last is the one every member of the space is
/// left holding. The stored aggregate said Online, the snapshot said Online, and the last event the
/// room received said Offline — one round in twenty on a two-core CI box, pinned by
/// <c>PresenceRaceTests.Switching_device_never_leaves_the_account_offline_while_the_new_device_is_online</c>.
/// No amount of atomicity at the store fixes that, because the race is between two publishes rather
/// than between two writes.</para>
///
/// <para><b>The fix is the grain, not a lock.</b> This is <see cref="IGrainWithGuidKey"/> keyed by
/// user id, and it is deliberately neither <c>[StatelessWorker]</c> nor <c>[Reentrant]</c>: Orleans
/// runs one turn of one activation at a time for a given key, so every fold, every hysteresis
/// decision and every fan-out for one user is serialised by the runtime — cluster-wide, without a
/// distributed lock, and without a script. Each turn recomputes the aggregate and publishes it
/// <em>inside the same turn</em>, so the last fan-out is by construction the one that saw the most
/// recent state.</para>
///
/// <para><b>It makes no grain calls, and that is a rule rather than an accident.</b> The fan-out
/// publishes straight through <c>AppHubServer</c> — which is exactly what <c>SpaceGrain.SetUserStatus</c>
/// and <c>SetUserPresence</c> do, one hop further along — because a single-activation grain that
/// called <c>ISpaceGrain</c> would deadlock against <c>SpaceGrain.UserJoined</c>, which awaits this
/// grain from inside its own turn on a join. <c>UserGrain</c> got away with that only by being a
/// worker pool; this one cannot, so it does not try.</para>
/// </remarks>
[Alias("Argon.Grains.Interfaces.IUserPresenceGrain")]
public interface IUserPresenceGrain : IGrainWithGuidKey
{
    /// <summary>
    /// Recomputes this user's aggregate and, if it moved, announces it to every space they are in and
    /// every friend they have.
    /// </summary>
    /// <remarks>
    /// The whole of a presence transition in one turn: fold, hysteresis, fan-out. Callers write their
    /// own session's status key first and then ask for this — the fold reads whatever is in Redis when
    /// it runs, so it can only ever see more than the caller wrote, never less.
    /// </remarks>
    [Alias(nameof(AggregateAndBroadcastStatusAsync))]
    ValueTask AggregateAndBroadcastStatusAsync(CancellationToken ct = default);

    /// <summary>
    /// The same, plus spaces that must be told the aggregate whether or not it changed.
    /// </summary>
    /// <remarks>
    /// <para>For a join. A space that has just gained a member has heard nothing about them ever, so
    /// "nothing changed" is the wrong answer for it and the right answer for everybody else: the
    /// per-user hysteresis record (<c>status:user:{u}:lastbroadcast</c>) exists to stop a re-asserted
    /// status being fanned out to spaces that already have it, and a seed is precisely the case it
    /// gets wrong.</para>
    ///
    /// <para>An Offline aggregate announces nothing at all, seeds included: a member who accepted an
    /// invite without ever opening the app has no status to report, and the space has no stale value
    /// to correct — see <c>SpaceGrain.UserJoined</c>.</para>
    /// </remarks>
    [Alias("AggregateAndBroadcastStatusAsyncSeeded")]
    ValueTask AggregateAndBroadcastStatusAsync(Guid[] seedSpaces, CancellationToken ct = default);

    /// <summary>
    /// Writes one session's status back after a lapse, and corrects observers if that moved the
    /// aggregate.
    /// </summary>
    /// <remarks>
    /// <para>The re-attach repair, and it lives here because it is a compare: read the aggregate,
    /// write the session's status, fold, and act on whether the two differ. Split across a caller and
    /// this grain those three steps would be interleavable by another session of the same user, which
    /// is the class of bug this grain exists to end — so the caller hands over the status and the
    /// decision is taken in one turn.</para>
    ///
    /// <para>When the aggregate did move, the hysteresis record is forgotten before the fan-out. A
    /// socket that dropped for longer than the session TTL and came back inside the grace leaves
    /// observers holding Offline while the record still says DoNotDisturb, so the corrective
    /// broadcast would be suppressed as a duplicate of an event nobody received — see
    /// <c>IUserPresenceService.ForgetLastBroadcastAsync</c>.</para>
    /// </remarks>
    [Alias(nameof(ReassertSessionStatusAsync))]
    ValueTask ReassertSessionStatusAsync(string sessionId, UserStatus status, CancellationToken ct = default);

    /// <summary>
    /// Folds the aggregate and writes it, telling nobody.
    /// </summary>
    /// <remarks>
    /// For the one caller that maintains its own fan-out: <c>BotGatewayGrain</c> announces a bot with
    /// direct <c>SpaceGrain.SetUserStatus</c> calls, deliberately and exactly once per transition
    /// (<c>PresenceBotTests.Installing_a_connected_bot_into_a_second_space_announces_it_online_once</c>),
    /// but the roster snapshot and the online counts read the aggregate — so the aggregate still has
    /// to be kept, and it still has to be folded from here, where a user's folds are ordered.
    /// </remarks>
    [Alias(nameof(RecalculateAggregatedStatusAsync))]
    ValueTask<UserStatus> RecalculateAggregatedStatusAsync(CancellationToken ct = default);

    /// <summary>Stores one session's activity and announces the user's representative one.</summary>
    /// <remarks>
    /// Ordered with the status fan-out rather than beside it, because a client that starts a game and
    /// changes status in the same breath produces two publishes to the same room, and two activations
    /// racing them is the same lost-order bug in a different shape.
    /// </remarks>
    [Alias(nameof(BroadcastPresenceAsync))]
    ValueTask BroadcastPresenceAsync(UserActivityPresence presence, string sessionId);

    /// <summary>Clears one session's activity and tells the rooms what is left.</summary>
    /// <remarks>
    /// <paramref name="alwaysBroadcast"/> true is the user (or the session tick) saying the activity
    /// is over: fan out the removal — or the other device's activity — even if this session's key had
    /// already lapsed, or observers keep a stale activity indefinitely. False is a session simply
    /// ending: announce only if it actually had an activity, so a disconnect of an activity-less user
    /// costs no events at all.
    /// </remarks>
    [Alias(nameof(RemoveBroadcastPresenceAsync))]
    ValueTask RemoveBroadcastPresenceAsync(string sessionId, bool alwaysBroadcast);
}
