namespace Argon.Grains;

using Argon.Api.Features.Bus;
using Features.Logic;
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
    IGrainFactory grainFactory,
    IClusterClient clusterClient,
    ILogger<IUserSessionGrain> logger,
    IUserPresenceService presenceService)
    : Grain, IUserSessionGrain, IRemindable
{
    private const string GraceReminderName = "presence-grace";

    private Guid   _userId;
    private string _sessionId = "";  // the stable per-launch sid (parsed from the grain key)

    private bool          _sessionStarted; // presence/status keys set up + counted for THIS activation
    private readonly HashSet<string> _connections = new();

    private IGrainTimer? refreshTimer;
    private UserStatus?  _preferredStatus;
    private DateTime?    _lastDebouncedHeartbeatTime;
    private DateTime?    _sessionStartTime;

    // Token bucket throttling status-change broadcasts: a single connection can otherwise flap its
    // status arbitrarily fast, and each change fans out to every server the user is in. Normal use
    // (a manual toggle, the ~3-min idle Online/Away transitions) never exhausts the bucket, so those
    // stay instant; sustained flapping is capped. A throttled change is simply dropped — the client
    // re-asserts its current status on the next ~15s heartbeat, by which point the bucket has refilled,
    // so the final state still propagates without letting a burst amplify into a broadcast storm.
    private const double StatusBucketCapacity   = 5;
    private const double StatusRefillPerSecond  = 0.5; // 1 token every 2s sustained
    private double       _statusTokens          = StatusBucketCapacity;
    private DateTime     _statusTokensUpdatedAt = DateTime.UtcNow;

    private string SessionId => _sessionId;

    private bool TryConsumeStatusToken()
    {
        var now = DateTime.UtcNow;
        _statusTokens = Math.Min(StatusBucketCapacity,
            _statusTokens + (now - _statusTokensUpdatedAt).TotalSeconds * StatusRefillPerSecond);
        _statusTokensUpdatedAt = now;

        if (_statusTokens < 1.0)
            return false;

        _statusTokens -= 1.0;
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

        // Only this activation's accounting is settled here. Crucially we do NOT remove Redis session
        // keys on arbitrary deactivation — their lifecycle is owned by GoOffline/finalize and the
        // presence TTL. Removing them here would defeat the disconnect grace.
        if (_sessionStarted)
        {
            if (_sessionStartTime.HasValue)
                UserSessionGrainInstrument.SessionDuration.Record((DateTime.UtcNow - _sessionStartTime.Value).TotalSeconds);

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
        if (_sessionStarted)
            return;

        _sessionStarted   = true;
        _preferredStatus  = preferred ?? UserStatus.Online;
        _sessionStartTime = DateTime.UtcNow;

        refreshTimer ??= this.RegisterGrainTimer(UserSessionTickAsync, TimeSpan.FromSeconds(15), TimeSpan.FromSeconds(15));

        await presenceService.SetSessionOnlineAsync(_userId, SessionId);
        await presenceService.SetSessionStatusAsync(_userId, SessionId, _preferredStatus.Value);
        await grainFactory.GetGrain<IUserGrain>(_userId).AggregateAndBroadcastStatusAsync();
        await grainFactory.GetGrain<IUserGrain>(_userId).UpdateUserDeviceHistory();

        logger.LogInformation("Session {sid} started for user {userId}", SessionId, _userId);

        UserSessionGrainInstrument.SessionsStarted.Add(1);
        UserSessionGrainInstrument.IncrementActiveSession();
    }

    public async ValueTask AttachConnectionAsync(string connectionId, UserStatus? preferredStatus = null)
    {
        await EnsureSessionStartedAsync(preferredStatus);

        _connections.Add(connectionId);
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
        if (_connections.Add(connectionId))
            await CancelGraceAsync();

        if (DateTime.UtcNow - (_lastDebouncedHeartbeatTime ?? DateTime.MinValue) > TimeSpan.FromSeconds(30))
        {
            _lastDebouncedHeartbeatTime = DateTime.UtcNow;
            await presenceService.HeartbeatAsync(_userId, SessionId);
        }

        if (status == UserStatus.Offline)
            status = UserStatus.Online;

        var statusTag = Tag(status);
        UserSessionGrainInstrument.Heartbeats.Add(1, new KeyValuePair<string, object?>("status", statusTag));

        if (_preferredStatus != status)
        {
            // Rate-limit status churn. On throttle, drop the change WITHOUT touching _preferredStatus or
            // Redis, so the next heartbeat re-detects the mismatch and propagates the final state once
            // the bucket refills — a burst can't amplify into a broadcast storm.
            if (!TryConsumeStatusToken())
            {
                logger.LogDebug("Throttled status change for session {sid} (user {userId})", SessionId, _userId);
            }
            else
            {
                UserSessionGrainInstrument.StatusChanges.Add(1,
                    new KeyValuePair<string, object?>("from_status", Tag(_preferredStatus ?? UserStatus.Online)),
                    new KeyValuePair<string, object?>("to_status", statusTag));

                _preferredStatus = status;
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
        _connections.Remove(connectionId);
        if (_connections.Count > 0)
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
        _connections.Clear();
        await FinalizeOfflineAsync(CancellationToken.None);
    }

    public async Task ReceiveReminder(string reminderName, TickStatus status)
    {
        if (reminderName != GraceReminderName)
            return;

        // Reconnected (or a heartbeat self-healed the set) — drop the grace.
        if (_connections.Count > 0)
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
        if (_connections.Count == 0)
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
