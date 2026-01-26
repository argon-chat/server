namespace Argon.Grains;

using Argon.Api.Features.Bus;
using Features.Logic;
using Instruments;
using MessagePipe;
using Orleans;
using Orleans.Concurrency;
using Orleans.Streams;
using Services;
using static DeactivationReasonCode;

public class UserSessionGrain(
    IGrainFactory grainFactory,
    IClusterClient clusterClient,
    ILogger<IUserSessionGrain> logger,
    IUserPresenceService presenceService,
    IArgonCacheDatabase cache,
    IRedisEventStorage eventStorage)
    : Grain, IUserSessionGrain
{
    private Guid   _userId;
    private string _machineId;
    private Guid   _shadowUserId;

    private IGrainTimer? refreshTimer;

    private UserStatus?  _preferredStatus;
    private IDisposable? _cacheSubscriber;

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
        
        _cacheSubscriber?.Dispose();
        refreshTimer?.Dispose();
        refreshTimer = null;

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
        var bag = DisposableBag.CreateBuilder();

        refreshTimer = this.RegisterGrainTimer(UserSessionTickAsync, TimeSpan.FromSeconds(15), TimeSpan.FromSeconds(15));

        eventStorage.OnKeyExpiredSubscribeAsync(OnKeyExpired).AddTo(bag);
        var servers = await grainFactory
           .GetGrain<IUserGrain>(_userId)
           .GetMyServersIds();
        await presenceService.SetSessionOnlineAsync(_userId, SessionId);
        await Task.WhenAll(servers.Select(server => 
            grainFactory
               .GetGrain<ISpaceGrain>(server)
               .SetUserStatus(_userId, _preferredStatus ?? UserStatus.Online)));
        _cacheSubscriber = bag.Build();

        await grainFactory.GetGrain<IUserGrain>(_userId).UpdateUserDeviceHistory();

        UserSessionGrainInstrument.SessionsStarted.Add(1);
        UserSessionGrainInstrument.IncrementActiveSession();
    }

    private static readonly TimeSpan ExpireGrace = TimeSpan.FromSeconds(10);

    private async ValueTask OnKeyExpired(OnRedisKeyExpired ev, CancellationToken ct = default)
    {
        var key = ev.key;
        if (!key.StartsWith($"presence:user:{_userId}:session:"))
            return;

        using var _ = logger.BeginScope("scope for {scopeType}, key: {key}, userId: {userId},  {sessionId}",
            "OnKeyExpired", key, _userId, SessionId);

        var now = DateTime.UtcNow;
        if (_lastHeartbeatTime is not null
            && now - _lastHeartbeatTime.Value <= (UserPresenceService.DefaultTTL - ExpireGrace))
        {
            logger.LogWarning("Ignore EXPIRE for session {sessionId}: last heartbeat {age} ago (< TTL {ttl} - grace {grace})",
                SessionId, now - _lastHeartbeatTime.Value, UserPresenceService.DefaultTTL, ExpireGrace);

            refreshTimer ??= this.RegisterGrainTimer(UserSessionTickAsync, TimeSpan.FromSeconds(15), TimeSpan.FromSeconds(15));
            return;
        }

        refreshTimer?.Dispose();
        refreshTimer = null;

        logger.LogInformation("Destroyed timer for session: {sessionId}, userId: {userId}", SessionId, _userId);

        if (!await presenceService.IsUserOnlineAsync(_userId, ct))
        {
            logger.LogInformation("This is last user session, become totally offline");
            var servers = await grainFactory.GetGrain<IUserGrain>(_userId).GetMyServersIds(ct);
            await Task.WhenAll(servers.Select(server =>
                grainFactory.GetGrain<ISpaceGrain>(server).SetUserStatus(_userId, UserStatus.Offline)));
            await grainFactory.GetGrain<IUserGrain>(_userId).RemoveBroadcastPresenceAsync();
            
            UserSessionGrainInstrument.Expirations.Add(1,
                new KeyValuePair<string, object?>("result", "offline"));
            
            logger.LogInformation("All necessary steps completed, self destroy called soon");
            await SelfDestroy();
            return;
        }

        UserSessionGrainInstrument.Expirations.Add(1,
            new KeyValuePair<string, object?>("result", "switch_session"));

        logger.LogInformation("This is not last user session, destroy only this session grain");
        await SelfDestroy();
    }

    private async Task UserSessionTickAsync(CancellationToken arg)
    {
        this.DelayDeactivation(TimeSpan.FromMinutes(2));
        var servers = await grainFactory
           .GetGrain<IUserGrain>(_userId)
           .GetMyServersIds(arg);
        await Task.WhenAll(servers.Select(server =>
            grainFactory
               .GetGrain<ISpaceGrain>(server)
               .SetUserStatus(_userId, _preferredStatus ?? UserStatus.Online)));

        if (!await presenceService.IsUserOnlineAsync(_userId, arg))
        {
            await Task.WhenAll(servers.Select(server =>
                grainFactory
                   .GetGrain<ISpaceGrain>(server)
                   .SetUserStatus(_userId, UserStatus.Offline)));
            refreshTimer?.Dispose();
            refreshTimer = null;
            await SelfDestroy();
            return;
        }
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
            await UserSessionTickAsync(CancellationToken.None);
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
        logger.LogInformation("Grain for session {sessionId} has been called EndRealtimeSession, go to expire session, linkedUserId: {userId}",
            SessionId, _shadowUserId);
        await cache.UpdateStringExpirationAsync($"presence:user:{_userId}:session:{SessionId}", TimeSpan.FromSeconds(60));
    }
}

