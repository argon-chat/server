namespace Argon.Grains;

using Argon.Api.Features.Bus;
using Features.Logic;
using Instruments;
using Orleans;
using Orleans.Concurrency;
using Services;
using static DeactivationReasonCode;

public class UserSessionGrain(
    IGrainFactory grainFactory,
    IClusterClient clusterClient,
    ILogger<IUserSessionGrain> logger,
    IUserPresenceService presenceService)
    : Grain, IUserSessionGrain
{
    private Guid   _userId;
    private string _machineId;
    private Guid   _shadowUserId;

    private IGrainTimer? refreshTimer;

    private UserStatus?  _preferredStatus;

    private DateTime? _lastHeartbeatTime;
    private DateTime? _lastDebouncedHeartbeatTime;
    private DateTime? _sessionStartTime;

    private string SessionId => this.GetPrimaryKeyString();

    private async ValueTask SelfDestroy()
        => GrainContext.Deactivate(new(ApplicationRequested, "omae wa mou shindeiru"));

    public async override Task OnDeactivateAsync(DeactivationReason reason, CancellationToken cancellationToken)
    {
        var isGraceful = reason.ReasonCode == ApplicationRequested;
        
        if (!isGraceful)
            logger.LogCritical("Alert, deactivation user session grain is not graceful!, {reason}", reason);
        
        logger.LogInformation("Grain for session {sessionId} has been shutdown, linkedUserId: {userId}", SessionId, _shadowUserId);
        
        refreshTimer?.Dispose();
        refreshTimer = null;

        // Clean up session status from Redis
        if (_userId != Guid.Empty)
        {
            try
            {
                await presenceService.RemoveSessionStatusAsync(_userId, SessionId, cancellationToken);
            }
            catch (Exception ex)
            {
                logger.LogWarning(ex, "Failed to remove session status during deactivation");
            }
        }

        // Record session duration
        if (_sessionStartTime.HasValue)
        {
            var duration = DateTime.UtcNow - _sessionStartTime.Value;
            UserSessionGrainInstrument.SessionDuration.Record(duration.TotalSeconds);
        }

        UserSessionGrainInstrument.SessionsEnded.Add(1,
            new KeyValuePair<string, object?>("reason", isGraceful ? "graceful" : "error"));
        
        UserSessionGrainInstrument.DecrementActiveSession();
    }

    public async ValueTask BeginRealtimeSession(UserStatus? preferredStatus = null)
    {
        _preferredStatus  = preferredStatus ?? UserStatus.Online;
        _userId           = this.GetUserId();
        _shadowUserId     = _userId;
        _machineId        = this.GetUserMachineId();
        _sessionStartTime = DateTime.UtcNow;

        logger.LogInformation("Grain for session {sessionId} has been activated, linkedUserId: {userId}", SessionId, _shadowUserId);

        _lastHeartbeatTime = DateTime.UtcNow;

        refreshTimer = this.RegisterGrainTimer(UserSessionTickAsync, TimeSpan.FromSeconds(15), TimeSpan.FromSeconds(15));
        
        await presenceService.SetSessionOnlineAsync(_userId, SessionId);
        await presenceService.SetSessionStatusAsync(_userId, SessionId, _preferredStatus.Value);
        await grainFactory.GetGrain<IUserGrain>(_userId).AggregateAndBroadcastStatusAsync();

        await grainFactory.GetGrain<IUserGrain>(_userId).UpdateUserDeviceHistory();

        UserSessionGrainInstrument.SessionsStarted.Add(1);
        UserSessionGrainInstrument.IncrementActiveSession();
    }

    private async Task UserSessionTickAsync(CancellationToken arg)
    {
        this.DelayDeactivation(TimeSpan.FromMinutes(2));

        // Check if our own presence key is still alive — if not, session is dead
        if (!await presenceService.IsSessionAliveAsync(_userId, SessionId, arg))
        {
            logger.LogInformation("Presence key expired for session {sessionId}, userId {userId} — cleaning up",
                SessionId, _userId);

            refreshTimer?.Dispose();
            refreshTimer = null;

            // Remove this session's status from Redis
            await presenceService.RemoveSessionStatusAsync(_userId, SessionId, arg);

            if (!await presenceService.IsUserOnlineAsync(_userId, arg))
            {
                logger.LogInformation("Last session for user {userId}, going totally offline", _userId);
                var servers = await grainFactory.GetGrain<IUserGrain>(_userId).GetMyServersIds(arg);
                await Task.WhenAll(servers.Select(server =>
                    grainFactory.GetGrain<ISpaceGrain>(server).SetUserStatus(_userId, UserStatus.Offline)));
                await grainFactory.GetGrain<IUserGrain>(_userId).RemoveBroadcastPresenceAsync();

                UserSessionGrainInstrument.Expirations.Add(1,
                    new KeyValuePair<string, object?>("result", "offline"));
            }
            else
            {
                // Re-aggregate status from remaining sessions
                await grainFactory.GetGrain<IUserGrain>(_userId).AggregateAndBroadcastStatusAsync(arg);

                UserSessionGrainInstrument.Expirations.Add(1,
                    new KeyValuePair<string, object?>("result", "switch_session"));
            }

            await SelfDestroy();
            return;
        }

        // Session is alive — refresh TTLs for both status and presence keys
        await presenceService.RefreshSessionStatusTtlAsync(_userId, SessionId, arg);
        await presenceService.HeartbeatAsync(_userId, SessionId, arg);
    }

    public async ValueTask<bool> HeartBeatAsync(UserStatus status)
    {
        if (_userId == Guid.Empty)
        {
            await BeginRealtimeSession(status);
            return false;
        }

        _lastHeartbeatTime = DateTime.UtcNow;
        if (DateTime.UtcNow - (_lastDebouncedHeartbeatTime ?? new DateTime()) > TimeSpan.FromSeconds(30))
        {
            _lastDebouncedHeartbeatTime = DateTime.UtcNow;
            await presenceService.HeartbeatAsync(_userId, SessionId);
        }

        if (status == UserStatus.Offline)
            status = UserStatus.Online;

        var statusTag = status switch
        {
            UserStatus.Online => "online",
            UserStatus.Away => "away",
            UserStatus.DoNotDisturb => "dnd",
            _ => "online"
        };

        UserSessionGrainInstrument.Heartbeats.Add(1,
            new KeyValuePair<string, object?>("status", statusTag));

        if (_preferredStatus != status)
        {
            var oldStatus = _preferredStatus ?? UserStatus.Online;
            var oldStatusTag = oldStatus switch
            {
                UserStatus.Online => "online",
                UserStatus.Away => "away",
                UserStatus.DoNotDisturb => "dnd",
                _ => "online"
            };

            UserSessionGrainInstrument.StatusChanges.Add(1,
                new KeyValuePair<string, object?>("from_status", oldStatusTag),
                new KeyValuePair<string, object?>("to_status", statusTag));

            _preferredStatus = status;
            
            // Update this session's status and re-aggregate through UserGrain
            await presenceService.SetSessionStatusAsync(_userId, SessionId, status);
            await grainFactory.GetGrain<IUserGrain>(_userId).AggregateAndBroadcastStatusAsync();
            await presenceService.HeartbeatAsync(_userId, SessionId);
        }
        DelayDeactivation(TimeSpan.FromMinutes(2));
        return true;
    }

    [OneWay]
    public ValueTask OnTypingEmit(Guid channelId)
        => this.GrainFactory.GetGrain<IChannelGrain>(channelId).OnTypingEmit();

    [OneWay]
    public ValueTask OnTypingStopEmit(Guid channelId)
        => this.GrainFactory.GetGrain<IChannelGrain>(channelId).OnTypingStopEmit();

    public async ValueTask EndRealtimeSession()
    {
        logger.LogInformation("Grain for session {sessionId} has been called EndRealtimeSession, linkedUserId: {userId}",
            SessionId, _shadowUserId);

        refreshTimer?.Dispose();
        refreshTimer = null;

        // Remove this session's status
        await presenceService.RemoveSessionStatusAsync(_userId, SessionId);

        if (!await presenceService.IsUserOnlineAsync(_userId))
        {
            // This was the last session - go offline
            var servers = await grainFactory.GetGrain<IUserGrain>(_userId).GetMyServersIds();
            await Task.WhenAll(servers.Select(server =>
                grainFactory.GetGrain<ISpaceGrain>(server).SetUserStatus(_userId, UserStatus.Offline)));
            await grainFactory.GetGrain<IUserGrain>(_userId).RemoveBroadcastPresenceAsync();
        }
        else
        {
            // Other sessions exist - re-aggregate status
            await grainFactory.GetGrain<IUserGrain>(_userId).AggregateAndBroadcastStatusAsync();
        }

        // Remove presence key
        await presenceService.RemoveSessionAsync(_userId, SessionId);
        
        await SelfDestroy();
    }
}

