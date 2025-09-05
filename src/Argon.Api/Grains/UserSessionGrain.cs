namespace Argon.Grains;

using System.Diagnostics.Metrics;
using Argon.Api.Features.Bus;
using Features.Logic;
using MessagePipe;
using Metrics;
using Metrics.Gauges;
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
    IRedisEventStorage eventStorage,
    IMetricsCollector metrics,
    ICounters globalCounters)
    : Grain, IUserSessionGrain, IAsyncObserver<IArgonEvent>
{
    private readonly MeasurementId    total_user_active       = new("total_user_active");
    private readonly RateCounter      heartbeatRate           = new(metrics, new("user_session_heartbeat_rate"));
    private readonly RateCounter      tickRate                = new(metrics, new("user_session_tick"));
    private readonly CountPerTagGauge sessionDestroyed        = new(metrics, new("user_session_destroyed"));
    private readonly CountPerTagGauge statusChangeCounter     = new(metrics, new("user_session_status_change"));
    private readonly CountPerTagGauge sessionHeartbeatCounter = new(metrics, new("user_session_heartbeat"));

    private Guid   _userId;
    private string _machineId;
    private Guid   _shadowUserId;

    private IDistributedArgonStream<IArgonEvent> userStream;

    private IGrainTimer? refreshTimer;

    private UserStatus?  _preferredStatus;
    private IDisposable? _cacheSubscriber;

    private DateTime? _lastHeartbeatTime;
    private DateTime? _lastDebouncedHeartbeatTime;

    public async ValueTask SelfDestroy()
        => GrainContext.Deactivate(new(ApplicationRequested, "omae wa mou shindeiru"));

    public async override Task OnDeactivateAsync(DeactivationReason reason, CancellationToken cancellationToken)
    {
        globalCounters.Decrement(total_user_active);
        var reasonTag = reason.ReasonCode == ApplicationRequested
            ? "graceful"
            : "force";
        _ = sessionDestroyed.CountAsync("reason", reasonTag);
        if (reason.ReasonCode != ApplicationRequested)
            logger.LogCritical("Alert, deactivation user session grain is not graceful!, {reason}", reason);
        logger.LogInformation("Grain for session {sessionId} has been shutdown, linkedUserId: {userId}", this.GetPrimaryKey(), _shadowUserId);
        _cacheSubscriber?.Dispose();
        refreshTimer?.Dispose();
        await userStream.DisposeAsync();
        refreshTimer = null;
    }

    public async ValueTask BeginRealtimeSession(UserStatus? preferredStatus = null)
    {
        globalCounters.Increment(total_user_active);
        _preferredStatus = preferredStatus;
        _userId          = this.GetUserId();
        _shadowUserId    = _userId;
        _machineId       = this.GetUserMachineId();

        logger.LogInformation("Grain for session {sessionId} has been activated, linkedUserId: {userId}", this.GetPrimaryKey(), _shadowUserId);

        _lastHeartbeatTime = DateTime.UtcNow;
        var bag = DisposableBag.CreateBuilder();


        userStream   = await this.Streams().CreateServerStreamFor(_userId);
        refreshTimer = this.RegisterGrainTimer(UserSessionTickAsync, TimeSpan.FromSeconds(15), TimeSpan.FromSeconds(15));

        eventStorage.OnKeyExpiredSubscribeAsync(OnKeyExpired).AddTo(bag);
        var servers = await grainFactory
           .GetGrain<IUserGrain>(_userId)
           .GetMyServersIds();
        await presenceService.SetSessionOnlineAsync(_userId, this.GetPrimaryKey());
        foreach (var server in servers)
            await grainFactory
               .GetGrain<ISpaceGrain>(server)
               .SetUserStatus(_userId, _preferredStatus ?? UserStatus.Online);
        _cacheSubscriber = bag.Build();

        await grainFactory.GetGrain<IUserGrain>(_userId).UpdateUserDeviceHistory();
    }

    private static readonly TimeSpan ExpireGrace = TimeSpan.FromSeconds(10);

    private async ValueTask OnKeyExpired(OnRedisKeyExpired ev, CancellationToken ct = default)
    {
        var key = ev.key;
        if (!key.StartsWith($"presence:user:{_userId}:session:"))
            return;

        using var _ = logger.BeginScope("scope for {scopeType}, key: {key}, userId: {userId},  {sessionId}",
            "OnKeyExpired", key, _userId, this.GetPrimaryKey());

        var now = DateTime.UtcNow;
        if (_lastHeartbeatTime is not null
            && now - _lastHeartbeatTime.Value <= (UserPresenceService.DefaultTTL - ExpireGrace))
        {
            logger.LogWarning("Ignore EXPIRE for session {sessionId}: last heartbeat {age} ago (< TTL {ttl} - grace {grace})",
                this.GetPrimaryKey(), now - _lastHeartbeatTime.Value, UserPresenceService.DefaultTTL, ExpireGrace);

            refreshTimer ??= this.RegisterGrainTimer(UserSessionTickAsync, TimeSpan.FromSeconds(15), TimeSpan.FromSeconds(15));
            return;
        }

        refreshTimer?.Dispose();
        refreshTimer = null;

        logger.LogInformation("Destroyed timer for session: {sessionId}, userId: {userId}", this.GetPrimaryKey(), _userId);

        if (!await presenceService.IsUserOnlineAsync(_userId, ct))
        {
            logger.LogInformation("This is last user session, become totally offline");
            var servers = await grainFactory.GetGrain<IUserGrain>(_userId).GetMyServersIds();
            foreach (var server in servers)
                await grainFactory.GetGrain<ISpaceGrain>(server).SetUserStatus(_userId, UserStatus.Offline);
            await grainFactory.GetGrain<IUserGrain>(_userId).RemoveBroadcastPresenceAsync();
            logger.LogInformation("All necessary steps completed, self destroy called soon");
            await SelfDestroy();
            return;
        }

        logger.LogInformation("This is not last user session, destroy only this session grain");
        await sessionDestroyed.CountAsync("reason", "expired_but_other_sessions_alive");
        await SelfDestroy();
    }

    private async Task UserSessionTickAsync(CancellationToken arg)
    {
        tickRate.Increment();
        this.DelayDeactivation(TimeSpan.FromMinutes(2));
        var servers = await grainFactory
           .GetGrain<IUserGrain>(_userId)
           .GetMyServersIds();
        foreach (var server in servers)
            await grainFactory
               .GetGrain<ISpaceGrain>(server)
               .SetUserStatus(_userId, _preferredStatus ?? UserStatus.Online);

        if (!await presenceService.IsUserOnlineAsync(_userId, arg))
        {
            foreach (var server in servers)
                await grainFactory
                   .GetGrain<ISpaceGrain>(server)
                   .SetUserStatus(_userId, UserStatus.Offline);
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
            await presenceService.HeartbeatAsync(_userId, this.GetPrimaryKey());
        }

        if (status == UserStatus.Offline)
            status = UserStatus.Online;

        if (_preferredStatus != status)
        {
            await statusChangeCounter.CountAsync("from", _preferredStatus?.ToString() ?? "unset");
            _preferredStatus = status;
            await UserSessionTickAsync(CancellationToken.None);
            await presenceService.HeartbeatAsync(_userId, this.GetPrimaryKey());
        }
        DelayDeactivation(TimeSpan.FromMinutes(2));
        heartbeatRate.Increment();
        await heartbeatRate.FlushAsync();
        await sessionHeartbeatCounter.CountAsync("status", status.ToString());
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
            this.GetPrimaryKey(), _shadowUserId);
        await cache.UpdateStringExpirationAsync($"presence:user:{_userId}:session:{this.GetPrimaryKey()}", TimeSpan.FromSeconds(60));
    }

    public async Task OnNextAsync(IArgonEvent item, StreamSequenceToken? token = null)
        => await userStream.Fire(item);

    public Task OnErrorAsync(Exception ex)
        => Task.CompletedTask;
}

public class ArgonDropConnectionException(string msg) : InvalidOperationException(msg);