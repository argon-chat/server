namespace Argon.Grains.Interfaces;

using Orleans.Concurrency;
using Users;

// Keyed by "{userId}:{sid}" — one grain per stable per-launch session id (sid), NOT per transport
// connection. A reconnect of the same client re-attaches to the same grain instead of churning a new
// one, which is what stops multi-device presence from flapping. The userId is encoded in the key so
// ReceiveReminder (which runs without a request context) can still resolve it.
[Alias($"Argon.Grains.Interfaces.{nameof(IUserSessionGrain)}")]
public interface IUserSessionGrain : IGrainWithStringKey
{
    /// <summary>
    /// A transport connection (SignalR ConnectionId) attached to this session. The first attach starts
    /// the session; further attaches (reconnect / second window) just join the live-connection set.
    /// Answers <c>false</c> when the attach was refused, i.e. the session has been signed out.
    /// </summary>
    /// <remarks>
    /// <para>The answer is the whole point of the return type, and it exists because the caller has
    /// already committed to the connection by the time this runs. <c>AppHub.OnConnectedAsync</c> gates
    /// on the revocation set itself, joins every <c>spaces/{id}</c> group and only then attaches; the
    /// grain re-reads the tombstones uncached, so it is the layer that catches a sign-out the hub's
    /// answer was too old to see. Returning <c>void</c> meant that catch was silent: the grain refused
    /// the session while the connection stayed open on every one of the user's space groups,
    /// receiving messages, presence and typing until the client's next heartbeat — up to fifteen
    /// seconds — tripped the second gate. The hub now aborts the connection on <c>false</c>.</para>
    ///
    /// <para>Refused means exactly one thing: the identity is tombstoned. The grain tests the presence
    /// sid it is keyed by, every credential session id recorded against it
    /// (<c>SessionRevocation.CredentialsKey</c>) and the user's sign-out-everywhere floor — see
    /// <c>SessionRevocation</c> for why a gate that tests only the first of those is escaped by
    /// choosing a new one. A store failure answers <c>true</c>, as everywhere else on an established
    /// path: a cache incident must not sign an instance out.</para>
    /// </remarks>
    [Alias(nameof(AttachConnectionAsync))]
    ValueTask<bool> AttachConnectionAsync(string connectionId, UserStatus? preferredStatus = null);

    // A transport connection dropped. When the last one goes, the session does NOT go offline
    // immediately — it arms a durable grace reminder and lets the presence TTL ride out transient
    // drops (OS sleep/modern-standby reconnects within the window). Reliable cleanup, no flap.
    [Alias(nameof(DetachConnectionAsync))]
    ValueTask DetachConnectionAsync(string connectionId);

    // Explicit, intentional offline for the WHOLE session (a device being signed out from the devices
    // screen): take it offline now, bypassing the grace window. Network drops use the grace;
    // deliberate exits are immediate.
    [Alias(nameof(GoOfflineAsync))]
    ValueTask GoOfflineAsync();

    /// <summary>
    /// One connection signing out: drop it, and end the session only if it was the last one.
    /// </summary>
    /// <remarks>
    /// <para>What the hub's <c>GoOffline</c> means, and it is not the same thing as the overload
    /// above. Signing out of one window used to finalize the entire session, publish Offline for a
    /// user whose other window was still connected and heartbeating, and then let that window's next
    /// heartbeat restart the session — an Offline/Online pair every roster and badge reacted to
    /// (defect S9, pinned by
    /// <c>PresenceLifecycleTests.A_window_signing_out_beside_a_live_window_of_the_same_session_does_not_flap_the_user</c>).</para>
    ///
    /// <para>A separate alias rather than an optional parameter so the session-wide call keeps its
    /// exact wire shape: <c>SecurityGrain.EndSessionAsync</c> genuinely means "end the whole session"
    /// and must not silently become per-connection.</para>
    /// </remarks>
    [Alias("GoOfflineFromConnectionAsync")]
    ValueTask GoOfflineAsync(string connectionId);

    [Alias(nameof(HeartBeatAsync))]
    ValueTask<bool> HeartBeatAsync(string connectionId, UserStatus status);

    /// <summary>
    /// This connection was just heard from: renew its lease, and nothing else.
    /// </summary>
    /// <remarks>
    /// <para>The session drops a connection nothing has been heard from for
    /// <c>PresenceTimingOptions.StaleConnectionAfter</c>, and dropping the last one arms the grace —
    /// the same path as a real detach. That floor was renewed by the attach and the heartbeat and by
    /// nothing else, while five other hub methods prove a transport is alive just as conclusively:
    /// <c>Resume</c>, both subscribes, both unsubscribes and every spelling of typing can only be
    /// called over a socket that is up. A backgrounded browser tab is the case that made the
    /// difference matter — the platform throttles its timers to roughly one wake a minute, so the
    /// application-level heartbeat is not a cadence the server may reason from — but the principle is
    /// the older one: liveness should be read from everything that proves it, not from the one call
    /// that was written to announce it.</para>
    ///
    /// <para><c>[OneWay]</c> because the answer is worth nothing to the caller and the cost has to be
    /// nothing on a path a client hits while somebody types. It also makes this the one shape of
    /// grain-interface addition a rolling deploy tolerates: a call landing on a silo running the
    /// previous build fails, and a one-way failure is not observed, so the worst a version skew can
    /// do is leave the lease renewed only by the heartbeat — which is where it started.</para>
    ///
    /// <para>Renews a lease; never creates one. A connection id this session does not hold, or a
    /// session that has not started, is a no-op — the stamp exists to keep a live transport counted,
    /// not to let one be asserted into existence.</para>
    /// </remarks>
    [OneWay, Alias(nameof(MarkConnectionSeenAsync))]
    ValueTask MarkConnectionSeenAsync(string connectionId);

    /// <summary>
    /// A heartbeat with no transport behind it: everything <see cref="HeartBeatAsync"/> does, except
    /// joining the live-connection set. Answers <c>false</c> when the session may not run at all.
    /// </summary>
    /// <remarks>
    /// <para>Exists because <c>IEventBus.Dispatch(HeartBeatEvent)</c> has no transport connection id
    /// and used to pass the sid itself as one. Nothing ever detached that pseudo-connection, so after
    /// every real socket dropped <c>DetachConnectionAsync</c> still saw <c>Count &gt; 0</c>: no grace
    /// was armed, the 15 s tick kept renewing the presence key and extending the activation, and the
    /// user was online until the silo restarted — one RPC away for anything holding a token (defect
    /// S10, pinned by
    /// <c>PresenceSessionGrainTests.An_Ion_dispatched_heartbeat_does_not_keep_a_disconnected_session_online</c>).</para>
    ///
    /// <para>The connection set is the whole of the difference, and it is enough: a session this RPC
    /// keeps alive has no attached transport, so the 15 s tick no-ops and its presence key lapses on
    /// its own 120 s TTL the moment the caller stops calling. That bounds a single Dispatch at one
    /// TTL instead of the life of the silo. It does not, on its own, publish an Offline when that
    /// lapse happens — nothing armed a grace, because nothing ever detached — so an Ion-only session
    /// that goes quiet leaves observers a stale status until they refetch. That is a smaller,
    /// separate hole in a legacy RPC no first-party client calls, and closing it means giving the
    /// Ion path a disconnect of its own rather than widening this one.</para>
    /// </remarks>
    [Alias(nameof(TouchAsync))]
    ValueTask<bool> TouchAsync(UserStatus status);


    [OneWay, Alias(nameof(OnTypingEmit))]
    ValueTask OnTypingEmit(Guid channelId);
    [OneWay, Alias(nameof(OnTypingStopEmit))]
    ValueTask OnTypingStopEmit(Guid channelId);

    public const string StorageId = "CacheStorage";
}

public class ArgonDropConnectionException(string msg) : InvalidOperationException(msg);
