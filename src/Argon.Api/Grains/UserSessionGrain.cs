namespace Argon.Grains;

using Argon.Api.Features.Bus;
using Features.Logic;
using Argon.Features.Auth;
using Argon.Features.Orleanse.Storages;
using Instruments;
using Orleans;
using Orleans.Concurrency;
using Orleans.Runtime;
using Services;
using static DeactivationReasonCode;

// One grain per stable session id (sid), keyed "{userId}:{sid}". Holds the set of live transport
// connections for that session. A reconnect of the same client re-attaches here instead of spawning a
// fresh grain, and the last connection dropping arms a durable grace reminder rather than going offline
// outright — together that stops multi-device presence from flapping while keeping offline reliable.
public class UserSessionGrain(
    // Stores nothing; it is here so the runtime carries this across a migration. See VolatileGrainStorage.
    [PersistentState("activation", VolatileGrainStorage.ProviderName)]
    IPersistentState<UserSessionActivationState> activation,
    IGrainFactory grainFactory,
    IClusterClient clusterClient,
    ILogger<IUserSessionGrain> logger,
    IUserPresenceService presenceService,
    IArgonCacheDatabase cache,
    IOptions<PresenceTimingOptions> timingOptions)
    : Grain, IUserSessionGrain, IRemindable
{
    private const string GraceReminderName = "presence-grace";

    /// <summary>
    /// Every interval this grain waits out, from configuration.
    /// </summary>
    /// <remarks>
    /// Bound once for the life of the activation rather than resolved per call: the tick period and
    /// the grace period are baked into a live timer and a live reminder the moment they are armed, so
    /// a reload underneath a running grain would leave it ticking on one value while claiming
    /// another. See <see cref="PresenceTimingOptions"/> for why these are one class and not nine
    /// constants scattered over two files.
    /// </remarks>
    private readonly PresenceTimingOptions timings = timingOptions.Value;

    private Guid   _userId;
    private string _sessionId = "";  // the stable per-launch sid (parsed from the grain key)


    private IGrainTimer? refreshTimer;

    /// <summary>
    /// The deadline that turns "connected, status not named yet" into "connected and Online".
    /// </summary>
    /// <remarks>
    /// <para>A hub attach carries no status (<c>AppHub.OnConnectedAsync</c> has none to carry), and
    /// answering that with an immediate Online is what announced a Do-Not-Disturb user as available
    /// to every space on each reconnect — defect S3, pinned by
    /// <c>PresenceRealtimeTests.A_returning_dnd_user_is_never_announced_online_to_the_space</c>. The
    /// session therefore starts alive but statusless and waits for the client to say what it is; the
    /// desktop client sends that heartbeat immediately on connect, so in practice nothing waits.</para>
    ///
    /// <para>The deadline exists because "statusless" must not be a place a session can live: a
    /// client that connects and never heartbeats would otherwise be invisible — present in the
    /// session index, Offline in every roster and every online count. So after
    /// <see cref="StatusDeadline"/> with a live connection and still no reported status, Online is
    /// assumed and announced once. That moves the optimistic default from "before we could possibly
    /// know" to "after we asked and were not answered", which is the whole of the fix.</para>
    /// </remarks>
    private IGrainTimer? statusDeadlineTimer;

    private TimeSpan RefreshPeriod  => timings.RefreshPeriod;
    private TimeSpan StatusDeadline => timings.StatusDeadline;

    /// <summary>How long a connection may go unheard from before this session stops counting it.</summary>
    /// <remarks>See <see cref="PruneStaleConnectionsAsync"/> for what the floor is for.</remarks>
    private TimeSpan StaleConnectionAfter => timings.StaleConnectionAfter;

    // Token bucket throttling status-change broadcasts: a single connection can otherwise flap its
    // status arbitrarily fast, and each change fans out to every server the user is in. Normal use
    // (a manual toggle, the ~3-min idle Online/Away transitions) never exhausts the bucket, so those
    // stay instant; sustained flapping is capped. A throttled change is simply dropped — the client
    // re-asserts its current status on the next ~15s heartbeat, by which point the bucket has refilled,
    // so the final state still propagates without letting a burst amplify into a broadcast storm.
    private const double StatusBucketCapacity   = 5;
    private const double StatusRefillPerSecond  = 0.5; // 1 token every 2s sustained


    private string SessionId => _sessionId;

    private bool TryConsumeStatusToken()
    {
        var now = DateTime.UtcNow;
        activation.State.StatusTokens = Math.Min(StatusBucketCapacity,
            activation.State.StatusTokens + (now - activation.State.StatusTokensUpdatedAt).TotalSeconds * StatusRefillPerSecond);
        activation.State.StatusTokensUpdatedAt = now;

        if (activation.State.StatusTokens < 1.0)
            return false;

        activation.State.StatusTokens -= 1.0;
        return true;
    }

    public override Task OnActivateAsync(CancellationToken cancellationToken)
    {
        // Grain key is "{userId}:{sid}". Parse both up front so ReceiveReminder (no request context)
        // can still resolve the user. Tolerate a bare key (legacy) by treating it all as the sid.
        var key = this.GetPrimaryKeyString();
        var sep = key.IndexOf(':');
        if (sep > 0 && Guid.TryParse(key[..sep], out var uid))
        {
            _userId    = uid;
            _sessionId = key[(sep + 1)..];
        }
        else
        {
            _sessionId = key;
        }

        // A fresh activation gets a full status budget; a migrated one keeps whatever it had left,
        // because the limit exists to stop status flapping and a move must not be a way around it.
        if (!activation.State.Activated)
        {
            activation.State.StatusTokens          = StatusBucketCapacity;
            activation.State.StatusTokensUpdatedAt = DateTime.UtcNow;
        }

        activation.State.Activated = true;

        // A migrated session did not restart, so nothing will call EnsureSessionStartedAsync again to
        // arm the refresh timer. Without this it stops renewing its presence keys and quietly goes
        // offline on the TTL, some minutes after a deployment nobody would connect it to.
        if (activation.State.SessionStarted)
            EnsureRefreshTimer();

        // And the same argument for the status deadline, which the same migration loses. A session
        // that attached and moved before its first heartbeat came up on the new silo started, live and
        // statusless with nothing left to answer for it: its presence key renewed every tick for ever
        // while `status:user:{u}:session:{sid}` was never written at all — connected, in the session
        // index, and Offline in every roster and every online count. See EnsureStatusDeadlineTimer.
        EnsureStatusDeadlineTimer();

        return Task.CompletedTask;
    }

    /// <summary>Arms the refresh tick (<see cref="PresenceTimingOptions.RefreshPeriod"/>) unless it is already running.</summary>
    /// <remarks>
    /// <para>Extracted so that every path which has (or regains) a live connection can re-arm it, not
    /// only a session start — defect S2. <see cref="DetachConnectionAsync"/> disposes the timer when
    /// the last connection goes, and the only re-arm sites used to be <see cref="OnActivateAsync"/>
    /// and <see cref="EnsureSessionStartedAsync"/>, which returns immediately once
    /// <c>SessionStarted</c> is set. A client that dropped and reconnected inside the grace therefore
    /// never renewed <c>status:user:{u}:session:{sid}</c> or <c>status:user:{u}:aggregated</c> again:
    /// both lapsed at the 120 s TTL and the user read Offline in every roster, every
    /// <c>GetMemberPresence</c> and every online count while sitting in the app connected and
    /// heartbeating, with no event to correct it.</para>
    ///
    /// <para>Safe to arm on a draining session, which is what makes this a one-line fix:
    /// <see cref="UserSessionTickAsync"/> returns at once while the connection set is empty, so the
    /// presence TTL still lapses and the grace reminder still finalizes. Pinned by
    /// <c>PresenceSessionGrainTests.A_session_that_reconnects_within_grace_keeps_renewing_like_one_that_never_dropped</c>
    /// and <c>PresenceLifecycleTests.A_session_that_reconnects_within_the_grace_keeps_refreshing_its_status_like_one_that_never_dropped</c>.</para>
    /// </remarks>
    private void EnsureRefreshTimer()
        => refreshTimer ??= this.RegisterGrainTimer(UserSessionTickAsync, RefreshPeriod, RefreshPeriod);

    /// <summary>
    /// Arms the status deadline whenever this session is started, connected and still statusless.
    /// </summary>
    /// <remarks>
    /// <para>The counterpart of <see cref="EnsureRefreshTimer"/>, and it exists for the same reason:
    /// the deadline used to be armed on exactly one path — the first-start branch of
    /// <see cref="EnsureSessionStartedAsync"/> — while three other paths could reach "started,
    /// connected, no status named" and had nothing to arm it. A reactivation or a migration inside
    /// the statusless window (<see cref="OnActivateAsync"/>), and a reconnect inside the grace to an
    /// already-started session (<see cref="AttachConnectionAsync"/>), both landed there. Nothing else
    /// writes the status key for a started session, so the session stayed statusless for good:
    /// present in the index and Offline everywhere a status is read.</para>
    ///
    /// <para>The guards are what make it safe to call from anywhere. A named status stands the
    /// deadline down rather than arming it, so the normal client — which heartbeats on connect — pays
    /// nothing; and it is not armed on a session with no connections, because
    /// <see cref="AnnounceAssumedStatusAsync"/> has nothing to assume for a session on its way out.
    /// <see cref="DetachConnectionAsync"/> disposes it with the refresh tick, which is what makes the
    /// deadline mean "this long after the connection that has to answer" rather than "this long after
    /// some connection once did".</para>
    /// </remarks>
    private void EnsureStatusDeadlineTimer()
    {
        if (!activation.State.SessionStarted || activation.State.PreferredStatus is not null)
            return;
        if (activation.State.Connections.Count == 0)
            return;

        statusDeadlineTimer ??= this.RegisterGrainTimer(AnnounceAssumedStatusAsync, StatusDeadline, StatusDeadline);
    }

    /// <summary>
    /// Whether this sid has been signed out, read straight from the revocation set.
    /// </summary>
    /// <remarks>
    /// <para>The second layer of defect S6. The hub gate (<c>AppHub</c>) can be bypassed — the Ion
    /// <c>IEventBus.Dispatch</c> path reaches this grain without touching the hub at all — so the
    /// grain refuses to <em>start</em> a revoked session itself, which makes "a revoked sid can never
    /// hold presence" true by construction rather than by every caller remembering to ask. Pinned by
    /// <c>PresenceRevocationTests.A_signed_out_device_stays_signed_out_once_the_revocation_reaches_presence</c>.</para>
    ///
    /// <para>Read uncached, unlike the hub's copy, and that is deliberate rather than sloppy: it runs
    /// once per session start and once per attach, never per heartbeat, so it costs a couple of set
    /// reads per connection — while a cached answer would leave a window in which the sign-out the
    /// user is watching for has not taken effect. Fails <em>open</em>, consistently with
    /// <c>ArgonTransactionInterceptor</c>: a store incident must not stop the whole instance from
    /// connecting.</para>
    ///
    /// <para><b>Three ids, not one, and the reason is the identity model in
    /// <see cref="SessionRevocation"/>.</b> The sid this grain is keyed by is the <em>presence</em>
    /// sid, which the caller writes — a label on the devices screen, not a credential — so a
    /// signed-out client escapes a gate that tests only it by minting a new one before it comes back.
    /// The unforgeable half is the credential sid the server puts inside the token, and the grain has
    /// no token to read it from; what it has is the bridge <c>SecurityGrain.EndSessionAsync</c>
    /// tombstones through, <see cref="SessionRevocation.CredentialsKey"/>, so every credential
    /// recorded against this presence session is tested beside it.</para>
    ///
    /// <para><b>And the floor, as far as the grain can honestly apply it.</b> The watermark ends every
    /// credential issued at or before it, and placing a credential in time needs its <c>iat</c> —
    /// which reaches <c>AppHub</c> in the ticket and stops there. The one thing the grain can date is
    /// the session itself, and that is enough for the case that matters: a session already running
    /// when the floor was written was necessarily authenticated with something minted before it, so
    /// the floor ends it. A session that has not started yet is left to the hub, because reading "I
    /// cannot place this in time" as "older than any floor" here would lock every user who has ever
    /// signed out everywhere out of presence permanently.</para>
    /// </remarks>
    private async Task<bool> IsSessionRevokedAsync()
    {
        // An identity the gate cannot parse is refused rather than waved through: every lookup below
        // is keyed on it, and "no id to look up" is not the same answer as "not revoked".
        if (!Guid.TryParse(SessionId, out var sid))
        {
            logger.LogWarning("Refusing session {sid} of user {userId}: the session id is not a guid, so no " +
                "revocation can name it", SessionId, _userId);
            return true;
        }

        try
        {
            var revoked = await cache.SetMembersAsync(SessionRevocation.RevokedKey(_userId));

            // The presence sid normalised the way every writer of the set writes it, plus every
            // credential id recorded against it. Normalised because the grain key is a string built by
            // concatenation from a claim: a future minting path emitting another guid format would
            // otherwise turn a load-bearing gate into a no-op while the hub's copy kept working.
            var identities = new List<Guid> { sid };

            foreach (var credential in await SessionRevocation.CredentialSessionsAsync(cache, _userId, sid))
            {
                if (Guid.TryParse(credential, out var credentialSessionId))
                    identities.Add(credentialSessionId);
            }

            foreach (var id in identities)
            {
                if (revoked.Contains(id.ToString()))
                    return true;

                // And the pre-set key shape, for the same reason the interceptor still reads it: a
                // revocation written before the set existed must not be forgotten by this deploy.
                if (await cache.KeyExistsAsync(SessionRevocation.LegacyRevokedKey(_userId, id)))
                    return true;
            }

            return activation.State.SessionStartTime is { } startedAt
                && SessionRevocation.IsBelowFloor(
                       SessionRevocation.ParseFloor(await cache.StringGetAsync(SessionRevocation.FloorKey(_userId))),
                       new DateTimeOffset(DateTime.SpecifyKind(startedAt, DateTimeKind.Utc)));
        }
        catch (Exception e)
        {
            logger.LogWarning(e, "Could not read the revocation set for session {sid} of user {userId}", SessionId, _userId);
            return false;
        }
    }

    private ValueTask SelfDestroy()
    {
        GrainContext.Deactivate(new(ApplicationRequested, "session ended"));
        return ValueTask.CompletedTask;
    }

    public override Task OnDeactivateAsync(DeactivationReason reason, CancellationToken cancellationToken)
    {
        refreshTimer?.Dispose();
        refreshTimer = null;
        statusDeadlineTimer?.Dispose();
        statusDeadlineTimer = null;

        // Migration is not the end of a session, it is the same session on another silo. Recording a
        // duration and decrementing the active-session gauge here would close a session that is still
        // open, and the target immediately counts it again — every rebalance would show up as churn
        // in the numbers that are supposed to measure real sign-outs.
        if (reason.ReasonCode == Migrating)
            return Task.CompletedTask;

        // Only this activation's accounting is settled here, and only if the session did not already
        // settle it on its way out (see MarkSessionEnded). Crucially we do NOT remove Redis session
        // keys on arbitrary deactivation — their lifecycle is owned by GoOffline/finalize and the
        // presence TTL. Removing them here would defeat the disconnect grace.
        if (activation.State.CountedActive)
        {
            var isGraceful = reason.ReasonCode == ApplicationRequested;
            if (!isGraceful)
                logger.LogWarning("UserSessionGrain {sid} (user {userId}) deactivated non-gracefully: {reason}",
                    SessionId, _userId, reason);

            MarkSessionEnded(isGraceful);
        }

        return Task.CompletedTask;
    }

    /// <summary>
    /// Starts the session on its first connection (or after a fresh (re)activation). Idempotent.
    /// Answers <c>false</c> when the session may not start at all, i.e. its sid has been signed out.
    /// </summary>
    /// <remarks>
    /// <para>Two halves that used to be one, split for defect S3: "this session is alive" and "this
    /// is the status its owner holds". A hub attach knows the first and cannot know the second —
    /// <c>AppHub.OnConnectedAsync</c> has no status to pass — and answering that with
    /// <c>preferred ?? Online</c> wrote Online into <c>status:user:{u}:session:{sid}</c> and fanned it
    /// out before the client had said anything, so a DND user was announced available to every space
    /// on every connect and only corrected a round trip later. With <paramref name="preferred"/> null
    /// the session now takes the alive half only — presence key, refresh tick, session accounting —
    /// leaves <c>PreferredStatus</c> null and lets the first heartbeat do the announcing, which it
    /// already did correctly. <see cref="statusDeadlineTimer"/> is what stops "not named yet" from
    /// becoming "never named".</para>
    ///
    /// <para>The Ion legacy path (<c>HeartBeatAsync</c> carrying a status) and bots are unaffected:
    /// they name a status, so both halves run exactly as before.</para>
    /// </remarks>
    private async Task<bool> EnsureSessionStartedAsync(UserStatus? preferred)
    {
        if (activation.State.SessionStarted)
            return true;

        if (await IsSessionRevokedAsync())
        {
            logger.LogWarning("Refused to start session {sid} for user {userId}: the session has been revoked",
                SessionId, _userId);
            return false;
        }

        // The records first, the state second. Flipping SessionStarted before the writes meant a
        // transient Redis failure left a session that believes it is started and has no presence key:
        // every later call short-circuits on that flag, the keep-alive paths are EXPIRE-only and
        // revive nothing, and the session spends its life online-but-invisible. Written this way a
        // throw leaves the session exactly as it was — not started, and therefore retryable by the
        // very next attach or heartbeat.
        await presenceService.SetSessionOnlineAsync(_userId, SessionId);

        if (preferred is { } named)
            await presenceService.SetSessionStatusAsync(_userId, SessionId, named);

        activation.State.SessionStarted   = true;
        activation.State.PreferredStatus  = preferred;
        activation.State.SessionStartTime = DateTime.UtcNow;

        EnsureRefreshTimer();
        // The statusless deadline is armed by the attach rather than here, because it must only run
        // for a session that has a connection to answer for it — and this runs before the connection
        // joins the set. See EnsureStatusDeadlineTimer.

        if (preferred is not null)
            await grainFactory.GetGrain<IUserPresenceGrain>(_userId).AggregateAndBroadcastStatusAsync();

        // Device history is no longer written from here: this grain is reached through the hub, whose
        // request context carries the ids but neither the address nor the country, so every row it
        // wrote said "unknown". PickTicket, which runs on the Ion path with the whole request in hand,
        // writes it once per session instead.

        logger.LogInformation("Session {sid} started for user {userId}", SessionId, _userId);

        UserSessionGrainInstrument.SessionsStarted.Add(1);
        MarkSessionActive();
        return true;
    }

    /// <summary>Counts this session into the silo's active gauge, once.</summary>
    /// <remarks>
    /// <see cref="UserSessionActivationState.CountedActive"/> rather than <c>SessionStarted</c> is what
    /// the closing side reads, because <see cref="FinalizeOfflineAsync"/> now resets the start flag
    /// while the activation is still alive — keying the decrement on it would leak one count per
    /// session ended, for ever.
    /// </remarks>
    private void MarkSessionActive()
    {
        if (activation.State.CountedActive)
            return;

        activation.State.CountedActive = true;
        UserSessionGrainInstrument.IncrementActiveSession();
    }

    /// <summary>Closes this session's accounting: its duration, its end and the active gauge.</summary>
    /// <remarks>
    /// Idempotent, and called from both endings a session has — the finalize it walks into itself and
    /// the deactivation that takes it by surprise — so a session is counted exactly once whichever
    /// one gets there first. A migration is neither and calls it not at all.
    /// </remarks>
    private void MarkSessionEnded(bool graceful)
    {
        if (!activation.State.CountedActive)
            return;

        activation.State.CountedActive = false;

        if (activation.State.SessionStartTime.HasValue)
            UserSessionGrainInstrument.SessionDuration.Record((DateTime.UtcNow - activation.State.SessionStartTime.Value).TotalSeconds);

        UserSessionGrainInstrument.SessionsEnded.Add(1,
            new KeyValuePair<string, object?>("reason", graceful ? "graceful" : "error"));
        UserSessionGrainInstrument.DecrementActiveSession();
    }

    /// <summary>
    /// The <see cref="StatusDeadline"/> expiring on a connected session that never named a status:
    /// assume Online, write it once, announce it once, and stand the timer down.
    /// </summary>
    /// <remarks>
    /// <para>Periodic rather than one-shot so that a failed announcement is retried: a Redis blip on
    /// the write below must not be the reason a session spends its life statusless. It stands itself
    /// down in every branch that means "there is nothing more for me to do" — a status now exists, the
    /// session is no longer started, the announcement landed, or nothing is attached any more — so the
    /// steady-state cost of a normal client, which heartbeats on connect, is one no-op tick.</para>
    ///
    /// <para>The state is committed <em>after</em> both writes, not before. Recording "Online" first
    /// and then failing to write it left the grain believing a status existed while
    /// <c>status:user:{u}:session:{sid}</c> held nothing: the next tick saw a status, stood the timer
    /// down, and every later heartbeat carrying Online found no change to apply — so the key was never
    /// written and the session was excluded from its own user's aggregate for as long as it lived.</para>
    /// </remarks>
    private async Task AnnounceAssumedStatusAsync(CancellationToken ct)
    {
        if (activation.State.PreferredStatus is not null || !activation.State.SessionStarted)
        {
            StandDownStatusDeadline();
            return;
        }

        // Nothing attached: the session is draining, and inventing a status for it would put an
        // Online on a user who is on their way out. Stand down rather than keep ticking — a detach
        // disposes this timer and a re-attach arms it again, so "asked again later" is the attach's
        // job, not a timer left running on a session with nobody on it.
        if (activation.State.Connections.Count == 0)
        {
            StandDownStatusDeadline();
            return;
        }

        logger.LogDebug("Session {sid} of user {userId} named no status within {deadline}; assuming Online",
            SessionId, _userId, StatusDeadline);

        // Not this callback's token: disposing the timer below cancels it, and the announcement has to
        // outlive the deadline that triggered it.
        await presenceService.SetSessionStatusAsync(_userId, SessionId, UserStatus.Online);
        await grainFactory.GetGrain<IUserPresenceGrain>(_userId).AggregateAndBroadcastStatusAsync();

        activation.State.PreferredStatus = UserStatus.Online;

        StandDownStatusDeadline();
    }

    private void StandDownStatusDeadline()
    {
        statusDeadlineTimer?.Dispose();
        statusDeadlineTimer = null;
    }

    /// <inheritdoc cref="IUserSessionGrain.MarkConnectionSeenAsync"/>
    [OneWay]
    public ValueTask MarkConnectionSeenAsync(string connectionId)
    {
        // A stamp for a connection this session does not hold would be a claim rather than a renewal,
        // and the set is the thing PruneStaleConnectionsAsync walks: an entry in ConnectionsLastSeen
        // with no member behind it is read by nobody and cleaned up by nothing.
        if (!activation.State.SessionStarted || !activation.State.Connections.Contains(connectionId))
            return ValueTask.CompletedTask;

        activation.State.ConnectionsLastSeen[connectionId] = DateTime.UtcNow;
        return ValueTask.CompletedTask;
    }

    /// <inheritdoc cref="IUserSessionGrain.AttachConnectionAsync"/>
    public async ValueTask<bool> AttachConnectionAsync(string connectionId, UserStatus? preferredStatus = null)
    {
        // The revocation gate, for the half EnsureSessionStartedAsync cannot reach: a session that is
        // already started short-circuits it, so a second window attaching to a session signed out
        // after it came up was never asked. Between the two, exactly one revocation read happens per
        // attach and none per heartbeat.
        if (activation.State.SessionStarted && await IsSessionRevokedAsync())
        {
            logger.LogWarning("Refused a connection on session {sid} of user {userId}: the session has been revoked",
                SessionId, _userId);

            // Not merely refused — ended. A tombstoned session must hold no presence at all, and the
            // sign-out that wrote the tombstone may have raced this attach.
            await GoOfflineAsync();
            return false;
        }

        if (!await EnsureSessionStartedAsync(preferredStatus))
        {
            // Signed out. Refuse the transport rather than quietly holding a dead session's
            // activation open; the hub aborts the connection on the answer (S6).
            await SelfDestroy();
            return false;
        }

        // Read before the set is touched: "the session had nothing attached and now has something"
        // is the reconnect the friends seed exists for, and it is the one thing the debounce below
        // never skips.
        var wasDetached = activation.State.Connections.Count == 0;

        MarkConnectionSeen(connectionId);
        // A connection is back — cancel any pending grace and (re)assert the presence key so a brief
        // lapse self-heals. The status is not RESET here, so a reconnect within grace keeps its real
        // status (no Online flash, no flap) — but it is re-asserted just below.
        await CancelGraceAsync();
        await presenceService.SetSessionOnlineAsync(_userId, SessionId);

        // Re-asserting the status is the other half of that self-healing, and the half that was
        // missing. Every keep-alive on the status side is an EXPIRE, which renews a key and revives
        // nothing, and a started session writes `status:user:{u}:session:{sid}` on exactly one path —
        // a heartbeat carrying a status that DIFFERS from the one already held. So a drop that
        // straddled the TTL came back with its presence key rewritten and its status key gone for
        // good: the client reconnected, kept heartbeating the same status it always had, and the user
        // read Offline in every roster, every snapshot and every online count with no event to correct
        // it. The presence grain re-folds `status:user:{u}:aggregated` on the way past, which is the
        // key the readers actually read.
        if (activation.State.PreferredStatus is { } known)
            await ReassertStatusAsync(known);

        // The tick is disposed when the last connection goes, and a re-attach is the moment it has to
        // come back. See EnsureRefreshTimer (S2) and EnsureStatusDeadlineTimer, which the same
        // argument applies to for a session that reconnects still statusless.
        EnsureRefreshTimer();
        EnsureStatusDeadlineTimer();

        // Seeded per connection, not per session (S14). Presence events only travel forward in time,
        // so a transport that came up after its friends did knows nothing about them — and a reconnect
        // inside the grace re-attaches to a started session, which used to skip this entirely and left
        // the returning client strictly worse informed than a cold start.
        //
        // Fired rather than awaited, because SignalR does not process a single client-to-server
        // invocation until OnConnectedAsync returns and OnConnectedAsync awaits this method. The push
        // is O(online friends) publishes, each a replay-stream append and a backplane hop; for a
        // well-connected user that is seconds, and every one of them is spent between the client
        // connecting and the client being able to say what its status is. The status deadline is
        // counting for that whole time, so an awaited push put the assumed-Online announcement BEFORE
        // the DND heartbeat it exists to wait for — reinstating, through the back door, exactly the
        // Online flash the deadline was introduced to remove.
        //
        // And debounced, because "per connection" is a budget anything holding a valid ticket can
        // spend: a connect loop on one sid buys a friends query, a session lookup per friend and one
        // replay-stream append per online friend, every time round. See FriendPushDebounce — the
        // reconnect the seed is actually for is exempt from it.
        if (wasDetached || DateTime.UtcNow - (activation.State.LastFriendPushAt ?? DateTime.MinValue) > timings.FriendPushDebounce)
        {
            activation.State.LastFriendPushAt = DateTime.UtcNow;
            PushFriendPresenceInBackground();
        }

        this.DelayDeactivation(timings.DeactivationDelay);
        return true;
    }

    /// <summary>
    /// Asks the presence grain to write this session's known status back and correct observers.
    /// </summary>
    /// <remarks>
    /// <para>The re-assert repairs the keys after a drop that straddled the TTL, and repairing keys is
    /// only half of it: an observer who read the roster during the lapse cached Offline, and nothing
    /// about a key coming back tells them otherwise. So the correction has to be asked for — and past
    /// the hysteresis, which is the part that made this silent rather than merely late.
    /// <c>MarkBroadcastIfChangedAsync</c> suppresses a fan-out that matches the last one recorded, and
    /// the record survives the lapse: it still says DoNotDisturb, so the broadcast that would have
    /// corrected every observer is dropped as a duplicate of an event they never received.</para>
    ///
    /// <para><b>Which is a compare, and a compare is why the whole of it moved.</b> Read the aggregate,
    /// write this session's status, fold, and act on whether the two differ — three steps this grain
    /// used to run itself, with another session of the same user free to interleave between any two of
    /// them. One turn of <see cref="IUserPresenceGrain.ReassertSessionStatusAsync"/> is the same
    /// sequence with nothing able to get inside it.</para>
    /// </remarks>
    private Task ReassertStatusAsync(UserStatus known)
        => grainFactory.GetGrain<IUserPresenceGrain>(_userId)
           .ReassertSessionStatusAsync(SessionId, known)
           .AsTask();

    /// <summary>Seeds the connecting client with its friends' presence, off this call's critical path.</summary>
    /// <remarks>
    /// The call is started inside this grain turn — so it carries the ambient request context and is
    /// ordered behind the writes above — and only the await is dropped. The continuation exists so a
    /// failure is a log line rather than an unobserved exception: nothing downstream depends on the
    /// push, since it is a convenience seed and every real transition is broadcast on its own.
    /// </remarks>
    private void PushFriendPresenceInBackground()
    {
        var push = grainFactory.GetGrain<IUserGrain>(_userId).PushFriendPresenceAsync();

        _ = ObserveAsync(push.AsTask());

        async Task ObserveAsync(Task pending)
        {
            try
            {
                await pending;
            }
            catch (Exception e)
            {
                logger.LogWarning(e, "Could not seed friend presence for session {sid} of user {userId}",
                    SessionId, _userId);
            }
        }
    }

    /// <summary>Records a connection as attached and heard from just now.</summary>
    /// <remarks>
    /// The stamp is what <see cref="PruneStaleConnectionsAsync"/> reads, and it rides beside the set
    /// rather than replacing it so that an activation migrating from a build without it still
    /// deserializes. Every add goes through here and every removal through
    /// <see cref="DropConnection"/>, which is what keeps the two from drifting apart.
    /// </remarks>
    private bool MarkConnectionSeen(string connectionId)
    {
        activation.State.ConnectionsLastSeen[connectionId] = DateTime.UtcNow;
        return activation.State.Connections.Add(connectionId);
    }

    /// <summary>Forgets a connection, stamp and all.</summary>
    private bool DropConnection(string connectionId)
    {
        activation.State.ConnectionsLastSeen.Remove(connectionId);
        return activation.State.Connections.Remove(connectionId);
    }

    /// <summary>Forgets every connection of this session.</summary>
    private void ClearConnections()
    {
        activation.State.Connections.Clear();
        activation.State.ConnectionsLastSeen.Clear();
    }

    public async ValueTask<bool> HeartBeatAsync(string connectionId, UserStatus status)
    {
        // Offline over a heartbeat is not a status, it is the absence of one (S12). The server refuses
        // to let a client make itself invisible this way, and that refusal used to be spelled
        // "status = Online" one line above the change check — which turned every stray Offline beat
        // into a deliberate status change that cleared the user's DoNotDisturb for everybody. Read as
        // "nothing reported" it keeps the liveness half and touches nothing else. Pinned by
        // PresenceRealtimeTests.A_heartbeat_carrying_offline_neither_hides_the_user_nor_changes_their_status.
        var reported = status == UserStatus.Offline ? null : (UserStatus?)status;

        // A heartbeat is the client speaking, so a session it starts is not statusless the way a hub
        // attach is — the client said something, it just said something the server will not honour.
        // A brand-new session whose only word was Offline therefore comes up Online, as it always has
        // (PresenceSessionGrainTests.Heartbeating_Offline_never_makes_a_live_session_offline); what
        // changed is that `reported` stays null below, so the value is never treated as a status
        // CHANGE against an already-established one.
        if (!await EnsureSessionStartedAsync(reported ?? UserStatus.Online))
            return false;

        // Self-heal the live-connection set from heartbeats — covers a reactivation that never saw the
        // attach, so a heartbeating client is never mistaken for a drained session. This is also
        // where a connection renews its lease: the stamp MarkConnectionSeen writes is what keeps it
        // out of the stale sweep (see PruneStaleConnectionsAsync).
        if (MarkConnectionSeen(connectionId))
            await CancelGraceAsync();

        EnsureRefreshTimer();

        await HeartBeatCoreAsync(reported);
        return true;
    }

    /// <summary>
    /// Everything a heartbeat does that is not about the transport set: renew the presence TTL,
    /// apply a reported status change, hold the activation open.
    /// </summary>
    /// <remarks>
    /// Split out for defect S10 so <see cref="TouchAsync"/> can reach it without joining
    /// <c>Connections</c>. Takes the <em>reported</em> status — null meaning "the client said
    /// nothing", which is what an Offline beat is read as.
    /// </remarks>
    private async Task HeartBeatCoreAsync(UserStatus? reported)
    {
        if (DateTime.UtcNow - (activation.State.LastDebouncedHeartbeatTime ?? DateTime.MinValue) > timings.HeartbeatDebounce)
        {
            activation.State.LastDebouncedHeartbeatTime = DateTime.UtcNow;
            await presenceService.HeartbeatAsync(_userId, SessionId);
        }

        // Tagged with what arrived, not with what we made of it, so an Offline beat is countable
        // instead of being filed as one more Online.
        UserSessionGrainInstrument.Heartbeats.Add(1,
            new KeyValuePair<string, object?>("status", reported is { } beat ? Tag(beat) : Tag(UserStatus.Offline)));

        if (reported is { } named && activation.State.PreferredStatus != named)
        {
            // Rate-limit status churn. On throttle, drop the change WITHOUT touching activation.State.PreferredStatus or
            // Redis, so the next heartbeat re-detects the mismatch and propagates the final state once
            // the bucket refills — a burst can't amplify into a broadcast storm.
            if (!TryConsumeStatusToken())
            {
                logger.LogDebug("Throttled status change for session {sid} (user {userId})", SessionId, _userId);
            }
            else
            {
                UserSessionGrainInstrument.StatusChanges.Add(1,
                    new KeyValuePair<string, object?>("from_status", Tag(activation.State.PreferredStatus ?? UserStatus.Online)),
                    new KeyValuePair<string, object?>("to_status", Tag(named)));

                activation.State.PreferredStatus = named;
                await presenceService.SetSessionStatusAsync(_userId, SessionId, named);
                await grainFactory.GetGrain<IUserPresenceGrain>(_userId).AggregateAndBroadcastStatusAsync();
                await presenceService.HeartbeatAsync(_userId, SessionId);
            }
        }

        this.DelayDeactivation(timings.DeactivationDelay);
    }

    /// <inheritdoc cref="IUserSessionGrain.TouchAsync"/>
    public async ValueTask<bool> TouchAsync(UserStatus status)
    {
        // It keeps a session alive; it does not bring one into being. A unary RPC has no transport
        // behind it, so a session started here would be a live row — presence key, session index, a
        // device on the user's own screen — that nothing is ever going to detach and no grace will
        // ever be armed for. It would drain on its TTL once the caller stopped calling, which bounds
        // it, but an observer is still shown a device that does not exist and the user is still shown
        // one they cannot end. Only the transport layer may start a session, for the same reason only
        // it may add to the connection set: it is the only layer that can also take it away.
        if (!activation.State.SessionStarted)
            return false;

        var reported = status == UserStatus.Offline ? null : (UserStatus?)status;

        // No EnsureSessionStartedAsync below it, and that is not an omission: the only thing it does
        // for a session that is already started is return true, and starting one is what the guard
        // above has just refused. A revoked session that is already running is gated where every
        // other established path is gated — ArgonTransactionInterceptor on the way in, exactly as
        // HeartBeatAsync leaves it to AppHub.
        //
        // The whole of the fix: a unary RPC has no lifetime a detach can hang on, so it never joins
        // the transport set. Everything else a heartbeat does, it does.
        await HeartBeatCoreAsync(reported);
        return true;
    }

    public async ValueTask DetachConnectionAsync(string connectionId)
    {
        DropConnection(connectionId);
        if (activation.State.Connections.Count > 0)
            return; // other connections of this session are still live — no status change

        await BeginGraceAsync();
    }

    /// <summary>
    /// The session has nothing attached: stop renewing, and let a durable reminder finalize it.
    /// </summary>
    /// <remarks>
    /// <para>Shared by the two ways a session can lose its last connection — the transport saying so
    /// (<see cref="DetachConnectionAsync"/>) and the transport saying nothing for long enough
    /// (<see cref="PruneStaleConnectionsAsync"/>) — because the two must be indistinguishable
    /// afterwards. Offline is deliberately not broadcast here: a transient drop (OS sleep/
    /// modern-standby, network blip) reconnects within the presence TTL and the status should ride it
    /// out. Not refreshing is what lets the TTL lapse if the device is really gone, and the reminder
    /// survives the grain deactivating — unlike a timer — which is what makes the offline reliable.</para>
    ///
    /// <para>The status deadline goes down with the tick. It used to keep firing across a detach, so a
    /// statusless session that dropped and came back inside the grace was announced Online by a timer
    /// armed for a connection that no longer existed — the DND flash the deadline was written to
    /// prevent, arriving a beat before the client's own status.</para>
    /// </remarks>
    private async Task BeginGraceAsync()
    {
        refreshTimer?.Dispose();
        refreshTimer = null;
        StandDownStatusDeadline();

        await this.RegisterOrUpdateReminder(GraceReminderName, timings.GracePeriod, timings.GracePeriod);
    }

    public async ValueTask GoOfflineAsync()
    {
        // Deliberate offline for the WHOLE session — skip the grace entirely. This is what
        // SecurityGrain.EndSessionAsync means by signing a device out, and it must stay session-wide.
        ClearConnections();
        await FinalizeOfflineAsync(CancellationToken.None);
    }

    /// <inheritdoc cref="IUserSessionGrain.GoOfflineAsync(string)"/>
    public async ValueTask GoOfflineAsync(string connectionId)
    {
        DropConnection(connectionId);

        // Another window of this session is still attached, so nothing about the user changed. The
        // session-wide finalize used to run here regardless (defect S9): it published Offline for a
        // user who had not gone anywhere, and the surviving window's next heartbeat landed on a fresh
        // activation and put them straight back — an Offline/Online pair every observer's roster
        // reacted to. Mirrors the guard DetachConnectionAsync already has.
        if (activation.State.Connections.Count > 0)
            return;

        // Last connection of the session: an explicit sign-out is immediate, with no grace. The
        // caller's own transport closing a moment later arrives as DetachConnectionAsync for an id
        // already removed, which is a no-op.
        await FinalizeOfflineAsync(CancellationToken.None);
    }

    public async Task ReceiveReminder(string reminderName, TickStatus status)
    {
        if (reminderName != GraceReminderName)
            return;

        // Reconnected (or a heartbeat self-healed the set) — drop the grace.
        if (activation.State.Connections.Count > 0)
        {
            await CancelGraceAsync();
            return;
        }

        // Still inside the TTL grace window (gone but the presence key hasn't lapsed yet) — wait for a
        // later reminder tick. Nothing refreshes the key while connections are empty, so it will lapse.
        if (await presenceService.IsSessionAliveAsync(_userId, SessionId))
            return;

        await FinalizeOfflineAsync(CancellationToken.None);
    }

    /// <summary>
    /// Tear this session down and re-broadcast the user's aggregate — Offline if it was the last
    /// session, otherwise whatever the remaining ones say.
    /// </summary>
    /// <remarks>
    /// <para>Routed through <c>AggregateAndBroadcastStatusAsync</c> so the hysteresis last-broadcast
    /// record stays consistent.</para>
    ///
    /// <para><b>Every step of the cleanup is best effort, and the ending is not.</b> This method is
    /// the only thing standing between a session and a zombie activation, and it used to be a straight
    /// line of awaits ending in <see cref="SelfDestroy"/> — so one grain call timing out (the voice
    /// sweep is a DB read and one hop per space) threw out of the middle of it. What was left behind
    /// was the worst of both endings: grace cancelled, timers disposed, presence keys deleted, and
    /// <c>SessionStarted</c> still true, which every later call short-circuits on. The activation then
    /// answered attaches and heartbeats without ever re-running the start path — no revocation gate,
    /// no <c>SetSessionOnline</c>, no status — for the rest of its life.</para>
    ///
    /// <para>So the activation is reset to "never started" first, before anything that can fail, and
    /// each cleanup step is allowed to fail on its own without taking the rest with it. The
    /// instrumentation and the deactivation at the end are unconditional, because a session that ends
    /// badly is still a session that ended.</para>
    /// </remarks>
    private async Task FinalizeOfflineAsync(CancellationToken ct)
    {
        await CancelGraceAsync();
        refreshTimer?.Dispose();
        refreshTimer = null;
        StandDownStatusDeadline();

        // The activation goes back to "never started" here rather than at the end, so that an
        // activation which survives this call (a throw below, a deactivation that does not land) is
        // one the next attach starts cleanly — revocation gate, presence key, status and all — rather
        // than one that believes it is already running and skips every one of them.
        // SessionStartTime is left in place until the accounting below has read it — it is the only
        // record of how long this session lasted.
        activation.State.SessionStarted             = false;
        activation.State.PreferredStatus            = null;
        activation.State.LastDebouncedHeartbeatTime = null;
        ClearConnections();

        // Remove this session's status AND presence/membership before reading IsUserOnlineAsync, so the
        // online check reflects only OTHER sessions (matters for the immediate GoOffline path where this
        // session's presence key is still alive). True is the conservative answer if that read never
        // happened: it costs a corrective broadcast, while a wrong false hangs up a call.
        var stillOnline = true;

        await BestEffortAsync("clear the presence records", async () =>
        {
            await presenceService.RemoveSessionStatusAsync(_userId, SessionId, ct);
            await presenceService.RemoveSessionAsync(_userId, SessionId, ct);

            stillOnline = await presenceService.IsUserOnlineAsync(_userId, ct);
        });

        await BestEffortAsync("re-broadcast the aggregate",
            () => grainFactory.GetGrain<IUserPresenceGrain>(_userId).AggregateAndBroadcastStatusAsync(ct).AsTask());

        // Clear THIS session's activity (per-session): if another device still shows an activity it
        // stays, this session's drops out. alwaysBroadcast=false → no fan-out for activity-less sessions
        // (avoids a removal storm on every disconnect).
        await BestEffortAsync("clear the session's activity",
            () => grainFactory.GetGrain<IUserPresenceGrain>(_userId).RemoveBroadcastPresenceAsync(SessionId, alwaysBroadcast: false).AsTask());

        // The user has no live session left anywhere, so they cannot be in a call either — defect
        // S15. Voice membership lives in ChannelGrain.Users and was emptied only by an explicit
        // DisconnectFromVoiceChannel, a moderator kick or the LiveKit webhook, so a client that quit,
        // crashed or was signed out left an occupant behind for everyone else to look at — and the
        // channel pins its own activation for a day while any occupant remains, so the ghost outlived
        // everything that could have cleaned it up. Gated on stillOnline precisely so that one device
        // of two signing out does not hang up the call the other device is in
        // (PresenceVoiceAndCountsTests.One_of_two_sessions_going_offline_leaves_the_call_alone).
        if (!stillOnline)
            await BestEffortAsync("leave the voice channels",
                () => grainFactory.GetGrain<IUserGrain>(_userId).LeaveAllVoiceAsync(ct).AsTask());

        UserSessionGrainInstrument.Expirations.Add(1,
            new KeyValuePair<string, object?>("result", stillOnline ? "switch_session" : "offline"));

        MarkSessionEnded(graceful: true);
        activation.State.SessionStartTime = null;

        logger.LogInformation("Session {sid} for user {userId} finalized offline (user stillOnline={stillOnline})",
            SessionId, _userId, stillOnline);

        await SelfDestroy();
    }

    /// <summary>Runs one cleanup step, and turns its failure into a log line instead of an ending.</summary>
    private async Task BestEffortAsync(string what, Func<Task> step)
    {
        try
        {
            await step();
        }
        catch (Exception e)
        {
            logger.LogWarning(e, "Could not {what} while finalizing session {sid} of user {userId}",
                what, SessionId, _userId);
        }
    }

    private async Task CancelGraceAsync()
    {
        if (await this.GetReminder(GraceReminderName) is { } reminder)
            await this.UnregisterReminder(reminder);
    }

    /// <summary>
    /// The session's keep-alive, and the sweeper for the one thing about a session that can end
    /// without anybody being told.
    /// </summary>
    /// <remarks>
    /// <para>The keep-alive half is the obvious one: push the presence and status TTLs back to full
    /// while a connection is attached, and stop doing so the moment none is, so the grace reminder can
    /// finalize a session that is really gone.</para>
    ///
    /// <para>The sweep is the other half, and it is here because there is nowhere better. An activity
    /// entry can end in two ways nobody hears: the key goes — evicted, or lapsed while this grain was
    /// not ticking — or the client stops re-announcing and its lease runs out. Redis emits no event
    /// for either, so the snapshot forgets the activity and everyone already in the room keeps
    /// rendering it, for the rest of their client session; two people in one room then disagree about
    /// whether a third is playing, permanently. The alternatives are a keyspace-notification
    /// subscriber (a second connection per silo, notifications off by default, and delivery that is
    /// best-effort by design) or a periodic scan of the keyspace (O(users) work to find O(0) of them
    /// most minutes). This costs one Redis read per tick per session that has announced something and
    /// zero for every session that has not, and it is already holding the sid, so it is the cheapest
    /// place in the product that can notice at all. Pinned by
    /// <c>PresenceActivityTests.An_activity_that_lapses_under_a_live_session_is_retracted_from_the_room</c>
    /// and <c>...An_activity_nobody_re_announces_is_retracted_when_its_lease_runs_out</c>.</para>
    ///
    /// <para>The retraction is asked of the presence grain rather than done here, for the reason every
    /// fan-out is: one activation per user publishes, in order. It is also what deletes the two keys,
    /// so a retraction that fails leaves the lease intact and the next tick asks again — at-least-once
    /// rather than at-most-once, and the second attempt is a no-op because the stamp is gone.</para>
    /// </remarks>
    private async Task UserSessionTickAsync(CancellationToken arg)
    {
        await PruneStaleConnectionsAsync();

        // While the session has no live connections it is draining: let the presence TTL lapse so the
        // grace reminder can finalize it. Refreshing here would keep a gone session "online" forever.
        if (activation.State.Connections.Count == 0)
            return;

        this.DelayDeactivation(timings.DeactivationDelay);
        var lease = await presenceService.RefreshSessionStatusTtlAsync(_userId, SessionId, arg);
        await presenceService.HeartbeatAsync(_userId, SessionId, arg);

        if (lease is not ActivityLeaseState.Expired)
            return;

        logger.LogInformation(
            "Session {sid} of user {userId} is still live but its activity is not: retracting it from the rooms",
            SessionId, _userId);

        // alwaysBroadcast, because by this point the key may well be gone already — which is precisely
        // one of the two cases being swept, and the case where "only announce if there was something"
        // would announce nothing at all.
        await grainFactory.GetGrain<IUserPresenceGrain>(_userId)
           .RemoveBroadcastPresenceAsync(SessionId, alwaysBroadcast: true);
    }

    /// <summary>
    /// Drops connections nothing has been heard from for <see cref="StaleConnectionAfter"/>, and arms
    /// the grace if that was the last of them.
    /// </summary>
    /// <remarks>
    /// <para><b>Why a session needs a liveness floor at all.</b> A connection id enters the set on an
    /// attach and leaves it on a detach, and the detach is not guaranteed. <c>AppHub</c> calls it from
    /// <c>OnDisconnectedAsync</c>, which can fault (a transient Orleans failure on the grain call, a
    /// silo shutting down mid-callback) or never run at all; nothing else in the product removes an
    /// id. What is left is self-sustaining in exactly the wrong direction: the tick keeps renewing
    /// <c>presence:user:{u}:session:{sid}</c> and extending the activation for as long as the set is
    /// non-empty, and a per-connection sign-out finds it non-empty and declines to finalize. So one
    /// lost callback pins a user Online to everyone, with a row on their own devices screen they
    /// cannot get rid of, until the silo restarts — the same shape as the immortal presence the Ion
    /// heartbeat path used to have, arriving by a different door.</para>
    ///
    /// <para>The floor makes "attached" a lease instead of a claim, and everything that could only
    /// have arrived over a live socket renews it: the heartbeat, the attach, and every gated hub
    /// method through <see cref="MarkConnectionSeenAsync"/>. The default is three minutes rather than
    /// the three heartbeats it looks like it should be, because the heartbeat's cadence is not the
    /// server's to assume — a backgrounded tab or a minimized window gets its timers throttled to
    /// roughly one wake a minute, so a floor sized on the un-throttled fifteen seconds pruned live
    /// clients and took them offline mid-session. See
    /// <see cref="PresenceTimingOptions.StaleConnectionAfter"/>. Losing the
    /// last one is deliberately indistinguishable from a detach — the same
    /// <see cref="BeginGraceAsync"/>, the same durable reminder, the same TTL and grace before anyone
    /// is told anything — because from the user's side it is a detach, one nobody reported.</para>
    ///
    /// <para>A connection carrying no stamp is stamped rather than swept. That is an activation from
    /// a build that did not write them, arriving by migration; reading "never heard from" as "stale"
    /// would take a perfectly live client offline on the first tick after a rebalance.</para>
    /// </remarks>
    private async Task PruneStaleConnectionsAsync()
    {
        if (activation.State.Connections.Count == 0)
            return;

        var now   = DateTime.UtcNow;
        var floor = StaleConnectionAfter;

        List<string>? stale = null;

        foreach (var connectionId in activation.State.Connections)
        {
            if (!activation.State.ConnectionsLastSeen.TryGetValue(connectionId, out var lastSeen))
            {
                activation.State.ConnectionsLastSeen[connectionId] = now;
                continue;
            }

            if (now - lastSeen <= floor)
                continue;

            (stale ??= []).Add(connectionId);
        }

        if (stale is null)
            return;

        foreach (var connectionId in stale)
            DropConnection(connectionId);

        logger.LogWarning(
            "Session {sid} of user {userId} dropped {count} connection(s) unheard from for more than {floor}; " +
            "{remaining} left attached", SessionId, _userId, stale.Count, floor, activation.State.Connections.Count);

        if (activation.State.Connections.Count == 0)
            await BeginGraceAsync();
    }

    [OneWay]
    public ValueTask OnTypingEmit(Guid channelId)
        => this.GrainFactory.GetGrain<IChannelGrain>(channelId).OnTypingEmit();

    [OneWay]
    public ValueTask OnTypingStopEmit(Guid channelId)
        => this.GrainFactory.GetGrain<IChannelGrain>(channelId).OnTypingStopEmit();

    private static string Tag(UserStatus s) => s switch
    {
        UserStatus.Online       => "online",
        UserStatus.Away         => "away",
        UserStatus.DoNotDisturb => "dnd",
        UserStatus.Offline      => "offline",
        _                       => "online"
    };
}

/// <summary>
/// What a user session activation holds that Redis does not.
/// </summary>
/// <remarks>
/// <para>The connection set is the reason this type exists. Presence keys in Redis survive a move on
/// their own TTL, but the list of live transports lives nowhere else — losing it leaves a session
/// that believes nobody is attached, arms the grace reminder and takes the user offline while their
/// client is still sitting there connected.</para>
///
/// <para>Held as <c>IPersistentState</c> against the storage that stores nothing, which is what
/// carries it across a migration without a line of code in this grain. See
/// <see cref="VolatileGrainStorage"/>.</para>
/// </remarks>
[GenerateSerializer]
public sealed record UserSessionActivationState
{
    /// <summary>Transport connection ids currently attached to this session.</summary>
    [Id(0)]
    public HashSet<string> Connections { get; set; } = [];

    /// <summary>Presence keys are set up and this session is counted as active.</summary>
    [Id(1)]
    public bool SessionStarted { get; set; }

    [Id(2)]
    public UserStatus? PreferredStatus { get; set; }

    [Id(3)]
    public DateTime? SessionStartTime { get; set; }

    [Id(4)]
    public DateTime? LastDebouncedHeartbeatTime { get; set; }

    /// <summary>
    /// The status-change budget, carried so a move does not hand the client a fresh one. The limit
    /// exists to stop status flapping, and refilling it on every rebalance is a way around it.
    /// </summary>
    [Id(5)]
    public double StatusTokens { get; set; }

    [Id(6)]
    public DateTime StatusTokensUpdatedAt { get; set; }

    /// <summary>
    /// Set once an activation has run. Absent on a fresh one, present on a migrated one — which is
    /// how the session tells a move from a restart.
    /// </summary>
    [Id(7)]
    public bool Activated { get; set; }

    /// <summary>
    /// When each attached connection was last heard from — an attach, or a heartbeat carrying its id.
    /// </summary>
    /// <remarks>
    /// Beside <see cref="Connections"/> rather than replacing it, so an activation migrating between
    /// a silo that writes these and one that does not still deserializes on both sides. The grain
    /// keeps the two in step through its own add/remove helpers, and treats a connection with no
    /// stamp as one just heard from. See <c>UserSessionGrain.PruneStaleConnectionsAsync</c> for what
    /// the stamps are for.
    /// </remarks>
    [Id(8)]
    public Dictionary<string, DateTime> ConnectionsLastSeen { get; set; } = [];

    /// <summary>
    /// When this session last seeded a connecting client with its friends' presence.
    /// </summary>
    /// <remarks>
    /// The debounce behind <c>PresenceTimingOptions.FriendPushDebounce</c>. In the activation rather
    /// than in Redis because it bounds work this activation is about to do, and a stamp that survived
    /// a migration would only mean the new silo declining a seed it never sent.
    /// </remarks>
    [Id(10)]
    public DateTime? LastFriendPushAt { get; set; }

    /// <summary>
    /// Whether this session is counted in the silo's active-sessions gauge.
    /// </summary>
    /// <remarks>
    /// Separate from <see cref="SessionStarted"/> because the two answer different questions and stop
    /// being true at different moments: the finalize resets the start flag while the activation is
    /// still there to be deactivated, and an accounting keyed on the start flag would then never
    /// decrement — one leaked count per session ended, on every silo, for ever.
    /// </remarks>
    [Id(9)]
    public bool CountedActive { get; set; }
}
