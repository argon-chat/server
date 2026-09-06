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
    IArgonCacheDatabase cache)
    : Grain, IUserSessionGrain, IRemindable
{
    private const string GraceReminderName = "presence-grace";

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

    private static readonly TimeSpan RefreshPeriod  = TimeSpan.FromSeconds(15);
    private static readonly TimeSpan StatusDeadline = TimeSpan.FromSeconds(5);

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

        return Task.CompletedTask;
    }

    /// <summary>Arms the 15 s refresh tick unless it is already running.</summary>
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
    /// Whether this sid has been signed out, read straight from the revocation set.
    /// </summary>
    /// <remarks>
    /// <para>The second layer of defect S6. The hub gate (<c>AppHub</c>) can be bypassed — the Ion
    /// <c>IEventBus.Dispatch</c> path reaches this grain without touching the hub at all — so the
    /// grain refuses to <em>start</em> a revoked session itself, which makes "a revoked sid can never
    /// hold presence" true by construction rather than by every caller remembering to ask. Pinned by
    /// <c>PresenceRevocationTests.A_signed_out_device_stays_signed_out_once_the_revocation_reaches_presence</c>.</para>
    ///
    /// <para>Read uncached, unlike the hub's copy, and that is deliberate rather than sloppy: this
    /// only runs from <see cref="EnsureSessionStartedAsync"/> on a session that is not started yet,
    /// so it costs one <c>SMEMBERS</c> per session start and nothing per heartbeat — while a cached
    /// answer would leave a window in which the sign-out the user is watching for has not taken
    /// effect. Fails <em>open</em>, consistently with <c>ArgonTransactionInterceptor</c>: a store
    /// incident must not stop the whole instance from connecting.</para>
    /// </remarks>
    private async Task<bool> IsSessionRevokedAsync()
    {
        if (!Guid.TryParse(SessionId, out var sid))
            return false;

        try
        {
            var key = SessionRevocation.RevokedKey(_userId);

            if ((await cache.SetMembersAsync(key)).Contains(SessionId))
                return true;

            // And the pre-set key shape, for the same reason the interceptor still reads it: a
            // revocation written before the set existed must not be forgotten by this deploy.
            return await cache.KeyExistsAsync(SessionRevocation.LegacyRevokedKey(_userId, sid));
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

        // Only this activation's accounting is settled here. Crucially we do NOT remove Redis session
        // keys on arbitrary deactivation — their lifecycle is owned by GoOffline/finalize and the
        // presence TTL. Removing them here would defeat the disconnect grace.
        if (activation.State.SessionStarted)
        {
            if (activation.State.SessionStartTime.HasValue)
                UserSessionGrainInstrument.SessionDuration.Record((DateTime.UtcNow - activation.State.SessionStartTime.Value).TotalSeconds);

            var isGraceful = reason.ReasonCode == ApplicationRequested;
            if (!isGraceful)
                logger.LogWarning("UserSessionGrain {sid} (user {userId}) deactivated non-gracefully: {reason}",
                    SessionId, _userId, reason);

            UserSessionGrainInstrument.SessionsEnded.Add(1,
                new KeyValuePair<string, object?>("reason", isGraceful ? "graceful" : "error"));
            UserSessionGrainInstrument.DecrementActiveSession();
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

        activation.State.SessionStarted   = true;
        activation.State.PreferredStatus  = preferred;
        activation.State.SessionStartTime = DateTime.UtcNow;

        EnsureRefreshTimer();

        await presenceService.SetSessionOnlineAsync(_userId, SessionId);

        if (preferred is { } named)
        {
            await presenceService.SetSessionStatusAsync(_userId, SessionId, named);
            await grainFactory.GetGrain<IUserGrain>(_userId).AggregateAndBroadcastStatusAsync();
        }
        else
            statusDeadlineTimer ??= this.RegisterGrainTimer(AnnounceAssumedStatusAsync, StatusDeadline, StatusDeadline);

        // Device history is no longer written from here: this grain is reached through the hub, whose
        // request context carries the ids but neither the address nor the country, so every row it
        // wrote said "unknown". PickTicket, which runs on the Ion path with the whole request in hand,
        // writes it once per session instead.

        logger.LogInformation("Session {sid} started for user {userId}", SessionId, _userId);

        UserSessionGrainInstrument.SessionsStarted.Add(1);
        UserSessionGrainInstrument.IncrementActiveSession();
        return true;
    }

    /// <summary>
    /// The <see cref="StatusDeadline"/> expiring on a connected session that never named a status:
    /// assume Online, write it once, announce it once, and stand the timer down.
    /// </summary>
    /// <remarks>
    /// Periodic rather than one-shot so that a session which reached the deadline with no live
    /// connection (attached, dropped, still inside the grace) is asked again rather than left
    /// statusless for good. It disposes itself the moment a status exists, so the steady-state cost
    /// of a normal client — which heartbeats on connect — is one no-op tick.
    /// </remarks>
    private async Task AnnounceAssumedStatusAsync(CancellationToken ct)
    {
        if (activation.State.PreferredStatus is not null || !activation.State.SessionStarted)
        {
            StandDownStatusDeadline();
            return;
        }

        // Nothing attached: the session is draining, and inventing a status for it would put an
        // Online on a user who is on their way out.
        if (activation.State.Connections.Count == 0)
            return;

        activation.State.PreferredStatus = UserStatus.Online;

        logger.LogDebug("Session {sid} of user {userId} named no status within {deadline}; assuming Online",
            SessionId, _userId, StatusDeadline);

        // Not this callback's token: disposing the timer below cancels it, and the announcement has to
        // outlive the deadline that triggered it.
        await presenceService.SetSessionStatusAsync(_userId, SessionId, UserStatus.Online);
        await grainFactory.GetGrain<IUserGrain>(_userId).AggregateAndBroadcastStatusAsync();

        StandDownStatusDeadline();
    }

    private void StandDownStatusDeadline()
    {
        statusDeadlineTimer?.Dispose();
        statusDeadlineTimer = null;
    }

    public async ValueTask AttachConnectionAsync(string connectionId, UserStatus? preferredStatus = null)
    {
        if (!await EnsureSessionStartedAsync(preferredStatus))
        {
            // Signed out. Refuse the transport rather than quietly holding a dead session's
            // activation open; the hub aborts the connection on its own gate (S6).
            await SelfDestroy();
            return;
        }

        activation.State.Connections.Add(connectionId);
        // A connection is back — cancel any pending grace and (re)assert the presence key so a brief
        // lapse self-heals. Status is NOT reset here, so a reconnect within grace keeps its real status
        // (no Online flash, no flap).
        await CancelGraceAsync();
        await presenceService.SetSessionOnlineAsync(_userId, SessionId);

        // The tick is disposed when the last connection goes, and a re-attach is the moment it has to
        // come back. See EnsureRefreshTimer (S2).
        EnsureRefreshTimer();

        // Seeded per connection, not per session (S14). Presence events only travel forward in time,
        // so a transport that came up after its friends did knows nothing about them — and a reconnect
        // inside the grace re-attaches to a started session, which used to skip this entirely and left
        // the returning client strictly worse informed than a cold start. Not on the heartbeat path:
        // this costs one friend-id query and one batched Redis read, which is the same order
        // AppHub.OnConnectedAsync already pays per connection.
        await grainFactory.GetGrain<IUserGrain>(_userId).PushFriendPresenceAsync();

        this.DelayDeactivation(TimeSpan.FromMinutes(2));
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
        // attach, so a heartbeating client is never mistaken for a drained session.
        if (activation.State.Connections.Add(connectionId))
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
        if (DateTime.UtcNow - (activation.State.LastDebouncedHeartbeatTime ?? DateTime.MinValue) > TimeSpan.FromSeconds(30))
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
                await grainFactory.GetGrain<IUserGrain>(_userId).AggregateAndBroadcastStatusAsync();
                await presenceService.HeartbeatAsync(_userId, SessionId);
            }
        }

        this.DelayDeactivation(TimeSpan.FromMinutes(2));
    }

    /// <inheritdoc cref="IUserSessionGrain.TouchAsync"/>
    public async ValueTask<bool> TouchAsync(UserStatus status)
    {
        var reported = status == UserStatus.Offline ? null : (UserStatus?)status;

        if (!await EnsureSessionStartedAsync(reported ?? UserStatus.Online))
            return false;

        // The whole of the fix: a unary RPC has no lifetime a detach can hang on, so it never joins
        // the transport set. Everything else a heartbeat does, it does.
        await HeartBeatCoreAsync(reported);
        return true;
    }

    public async ValueTask DetachConnectionAsync(string connectionId)
    {
        activation.State.Connections.Remove(connectionId);
        if (activation.State.Connections.Count > 0)
            return; // other connections of this session are still live — no status change

        // Last connection dropped. Don't broadcast offline now: a transient drop (OS sleep/
        // modern-standby, network blip) reconnects within the presence TTL and we want the status to
        // ride it out. Stop refreshing so the TTL can lapse if the device is really gone, and arm a
        // durable grace reminder (survives grain deactivation — unlike a timer) to finalize offline.
        refreshTimer?.Dispose();
        refreshTimer = null;
        await this.RegisterOrUpdateReminder(GraceReminderName, TimeSpan.FromMinutes(1), TimeSpan.FromMinutes(1));
    }

    public async ValueTask GoOfflineAsync()
    {
        // Deliberate offline for the WHOLE session — skip the grace entirely. This is what
        // SecurityGrain.EndSessionAsync means by signing a device out, and it must stay session-wide.
        activation.State.Connections.Clear();
        await FinalizeOfflineAsync(CancellationToken.None);
    }

    /// <inheritdoc cref="IUserSessionGrain.GoOfflineAsync(string)"/>
    public async ValueTask GoOfflineAsync(string connectionId)
    {
        activation.State.Connections.Remove(connectionId);

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

    // Tear this session down and re-broadcast the user's aggregate (Offline if it was the last session,
    // otherwise the remaining sessions' status). Routed through AggregateAndBroadcastStatusAsync so the
    // hysteresis last-broadcast record stays consistent.
    private async Task FinalizeOfflineAsync(CancellationToken ct)
    {
        await CancelGraceAsync();
        refreshTimer?.Dispose();
        refreshTimer = null;
        statusDeadlineTimer?.Dispose();
        statusDeadlineTimer = null;

        // Remove this session's status AND presence/membership before reading IsUserOnlineAsync, so the
        // online check reflects only OTHER sessions (matters for the immediate GoOffline path where this
        // session's presence key is still alive).
        await presenceService.RemoveSessionStatusAsync(_userId, SessionId, ct);
        await presenceService.RemoveSessionAsync(_userId, SessionId, ct);

        var stillOnline = await presenceService.IsUserOnlineAsync(_userId, ct);
        await grainFactory.GetGrain<IUserGrain>(_userId).AggregateAndBroadcastStatusAsync(ct);
        // Clear THIS session's activity (per-session): if another device still shows an activity it
        // stays, this session's drops out. alwaysBroadcast=false → no fan-out for activity-less sessions
        // (avoids a removal storm on every disconnect).
        await grainFactory.GetGrain<IUserGrain>(_userId).RemoveBroadcastPresenceAsync(SessionId, alwaysBroadcast: false);

        // The user has no live session left anywhere, so they cannot be in a call either — defect
        // S15. Voice membership lives in ChannelGrain.Users and was emptied only by an explicit
        // DisconnectFromVoiceChannel, a moderator kick or the LiveKit webhook, so a client that quit,
        // crashed or was signed out left an occupant behind for everyone else to look at — and the
        // channel pins its own activation for a day while any occupant remains, so the ghost outlived
        // everything that could have cleaned it up. Gated on stillOnline precisely so that one device
        // of two signing out does not hang up the call the other device is in
        // (PresenceVoiceAndCountsTests.One_of_two_sessions_going_offline_leaves_the_call_alone).
        if (!stillOnline)
            await grainFactory.GetGrain<IUserGrain>(_userId).LeaveAllVoiceAsync(ct);

        UserSessionGrainInstrument.Expirations.Add(1,
            new KeyValuePair<string, object?>("result", stillOnline ? "switch_session" : "offline"));

        logger.LogInformation("Session {sid} for user {userId} finalized offline (user stillOnline={stillOnline})",
            SessionId, _userId, stillOnline);

        await SelfDestroy();
    }

    private async Task CancelGraceAsync()
    {
        if (await this.GetReminder(GraceReminderName) is { } reminder)
            await this.UnregisterReminder(reminder);
    }

    private async Task UserSessionTickAsync(CancellationToken arg)
    {
        // While the session has no live connections it is draining: let the presence TTL lapse so the
        // grace reminder can finalize it. Refreshing here would keep a gone session "online" forever.
        if (activation.State.Connections.Count == 0)
            return;

        this.DelayDeactivation(TimeSpan.FromMinutes(2));
        await presenceService.RefreshSessionStatusTtlAsync(_userId, SessionId, arg);
        await presenceService.HeartbeatAsync(_userId, SessionId, arg);
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
}
