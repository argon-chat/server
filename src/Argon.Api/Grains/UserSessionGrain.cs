namespace Argon.Grains;

using Argon.Api.Features.Bus;
using Features.Logic;
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
    IUserPresenceService presenceService)
    : Grain, IUserSessionGrain, IRemindable
{
    private const string GraceReminderName = "presence-grace";

    private Guid   _userId;
    private string _sessionId = "";  // the stable per-launch sid (parsed from the grain key)


    private IGrainTimer? refreshTimer;

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
            refreshTimer ??= this.RegisterGrainTimer(UserSessionTickAsync,
                TimeSpan.FromSeconds(15), TimeSpan.FromSeconds(15));

        return Task.CompletedTask;
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

    // Start the session on its first connection (or after a fresh (re)activation). Idempotent.
    private async Task EnsureSessionStartedAsync(UserStatus? preferred)
    {
        if (activation.State.SessionStarted)
            return;

        activation.State.SessionStarted   = true;
        activation.State.PreferredStatus  = preferred ?? UserStatus.Online;
        activation.State.SessionStartTime = DateTime.UtcNow;

        refreshTimer ??= this.RegisterGrainTimer(UserSessionTickAsync, TimeSpan.FromSeconds(15), TimeSpan.FromSeconds(15));

        await presenceService.SetSessionOnlineAsync(_userId, SessionId);
        await presenceService.SetSessionStatusAsync(_userId, SessionId, activation.State.PreferredStatus.Value);
        await grainFactory.GetGrain<IUserGrain>(_userId).AggregateAndBroadcastStatusAsync();
        await grainFactory.GetGrain<IUserGrain>(_userId).PushFriendPresenceAsync();
        await grainFactory.GetGrain<IUserGrain>(_userId).UpdateUserDeviceHistory();

        logger.LogInformation("Session {sid} started for user {userId}", SessionId, _userId);

        UserSessionGrainInstrument.SessionsStarted.Add(1);
        UserSessionGrainInstrument.IncrementActiveSession();
    }

    public async ValueTask AttachConnectionAsync(string connectionId, UserStatus? preferredStatus = null)
    {
        await EnsureSessionStartedAsync(preferredStatus);

        activation.State.Connections.Add(connectionId);
        // A connection is back — cancel any pending grace and (re)assert the presence key so a brief
        // lapse self-heals. Status is NOT reset here, so a reconnect within grace keeps its real status
        // (no Online flash, no flap).
        await CancelGraceAsync();
        await presenceService.SetSessionOnlineAsync(_userId, SessionId);

        this.DelayDeactivation(TimeSpan.FromMinutes(2));
    }

    public async ValueTask<bool> HeartBeatAsync(string connectionId, UserStatus status)
    {
        await EnsureSessionStartedAsync(status);

        // Self-heal the live-connection set from heartbeats — covers a reactivation that never saw the
        // attach, so a heartbeating client is never mistaken for a drained session.
        if (activation.State.Connections.Add(connectionId))
            await CancelGraceAsync();

        if (DateTime.UtcNow - (activation.State.LastDebouncedHeartbeatTime ?? DateTime.MinValue) > TimeSpan.FromSeconds(30))
        {
            activation.State.LastDebouncedHeartbeatTime = DateTime.UtcNow;
            await presenceService.HeartbeatAsync(_userId, SessionId);
        }

        if (status == UserStatus.Offline)
            status = UserStatus.Online;

        var statusTag = Tag(status);
        UserSessionGrainInstrument.Heartbeats.Add(1, new KeyValuePair<string, object?>("status", statusTag));

        if (activation.State.PreferredStatus != status)
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
                    new KeyValuePair<string, object?>("to_status", statusTag));

                activation.State.PreferredStatus = status;
                await presenceService.SetSessionStatusAsync(_userId, SessionId, status);
                await grainFactory.GetGrain<IUserGrain>(_userId).AggregateAndBroadcastStatusAsync();
                await presenceService.HeartbeatAsync(_userId, SessionId);
            }
        }

        this.DelayDeactivation(TimeSpan.FromMinutes(2));
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
        // Deliberate offline — skip the grace entirely.
        activation.State.Connections.Clear();
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
