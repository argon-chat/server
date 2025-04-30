namespace Argon.Grains;

using Features.Logic;
using Features.Rpc;
using MessagePipe;
using Orleans.Streams;
using Services;
using static DeactivationReasonCode;

public class UserSessionGrain(
    IGrainFactory grainFactory,
    IClusterClient clusterClient,
    ILogger<IUserSessionGrain> logger,
    IUserPresenceService presenceService,
    IArgonCacheDatabase cache,
    IAsyncSubscriber<OnRedisKeyExpired> subscriberKeyExpired)
    : Grain, IUserSessionGrain, IAsyncObserver<IArgonEvent>
{
    private Guid _userId;
    private Guid _machineId;

    private IArgonStream<IArgonEvent> userStream;

    private IGrainTimer? refreshTimer;

    private UserStatus?  _preferredStatus;
    private IDisposable? _cacheSubscriber;

    private DateTime? _lastHeartbeatTime;
    private DateTime? _lastDebouncedHeartbeatTime;

    public async ValueTask SelfDestroy()
        => GrainContext.Deactivate(new(ApplicationRequested, "omae wa mou shindeiru"));

    public async override Task OnDeactivateAsync(DeactivationReason reason, CancellationToken cancellationToken)
    {
        if (reason.ReasonCode != ApplicationRequested)
            logger.LogCritical("Alert, deactivation user session grain is not graceful!, {reason}", reason);
        _cacheSubscriber?.Dispose();
        refreshTimer?.Dispose();
        refreshTimer = null;
    }

    public async ValueTask BeginRealtimeSession(Guid userId, Guid machineKey, UserStatus? preferredStatus = null)
    {
        if (_userId != Guid.Empty && _machineId != Guid.Empty)
        {
            logger.LogWarning("Trying activate session, but session already active, {sessionId}, {userId}", this.GetPrimaryKey(), _userId);
            return;
        }

        _userId            = userId;
        _machineId         = machineKey;
        _lastHeartbeatTime = DateTime.UtcNow;
        var bag = DisposableBag.CreateBuilder();


        userStream   = await this.Streams().CreateServerStreamFor(_userId);
        refreshTimer = this.RegisterGrainTimer(UserSessionTickAsync, TimeSpan.FromSeconds(15), TimeSpan.FromSeconds(15));

        subscriberKeyExpired.Subscribe(OnKeyExpired).AddTo(bag);
        var servers = await grainFactory
           .GetGrain<IUserGrain>(_userId)
           .GetMyServersIds();
        await presenceService.SetSessionOnlineAsync(_userId, this.GetPrimaryKey());
        foreach (var server in servers)
            await grainFactory
               .GetGrain<IServerGrain>(server)
               .SetUserStatus(_userId, _preferredStatus ?? UserStatus.Online);
        _cacheSubscriber = bag.Build();
    }

    private async ValueTask OnKeyExpired(OnRedisKeyExpired ev, CancellationToken ct = default)
    {
        var key = ev.key;
        if (!key.StartsWith($"presence:user:{_userId}:session:"))
            return;
        using var _ = logger.BeginScope("scope for {scopeType}, key: {key}, userId: {userId},  {sessionId}", "OnKeyExpired", key, _userId,
            this.GetPrimaryKey());

        refreshTimer?.Dispose();
        refreshTimer = null;

        logger.LogInformation("Destroyed timer for session: {sessionId}, userId: {userId}", this.GetPrimaryKey(), _userId);

        if (!await presenceService.IsUserOnlineAsync(_userId, ct))
        {
            logger.LogInformation("This is last user session, become totally offline");
            var servers = await grainFactory
               .GetGrain<IUserGrain>(_userId)
               .GetMyServersIds();
            foreach (var server in servers)
                await grainFactory
                   .GetGrain<IServerGrain>(server)
                   .SetUserStatus(_userId, UserStatus.Offline);
            await grainFactory
               .GetGrain<IUserGrain>(_userId)
               .RemoveBroadcastPresenceAsync();
            logger.LogInformation("All necessary steps completed, self destroy called soon");
            await this.SelfDestroy();
            return;
        }

        logger.LogInformation("This is not last user session, skip offline broadcast, destroy session...");


        if (_lastHeartbeatTime is null)
        {
            logger.LogCritical($"ALERT, detected dead session, but lastHeartbeatTime not defined");
            await this.SelfDestroy();
            return;
        }

        if (_lastHeartbeatTime - DateTime.UtcNow > UserPresenceService.DefaultTTL)
        {
            logger.LogInformation("Session is now graceful complete, predicate {time} > {defaultTTL} return true",
                _lastHeartbeatTime - DateTime.UtcNow, UserPresenceService.DefaultTTL);
            await this.SelfDestroy();
            return;
        }

        logger.LogCritical($"ALERT, detected dead session, but lastHeartbeatTime less default TTL");
        await this.SelfDestroy();
    }

    private async Task UserSessionTickAsync(CancellationToken arg)
    {
        this.DelayDeactivation(TimeSpan.FromMinutes(2));
        var servers = await grainFactory
           .GetGrain<IUserGrain>(_userId)
           .GetMyServersIds();
        foreach (var server in servers)
            await grainFactory
               .GetGrain<IServerGrain>(server)
               .SetUserStatus(_userId, _preferredStatus ?? UserStatus.Online);

        if (!await presenceService.IsUserOnlineAsync(_userId, arg))
        {
            foreach (var server in servers)
                await grainFactory
                   .GetGrain<IServerGrain>(server)
                   .SetUserStatus(_userId, UserStatus.Offline);
            refreshTimer?.Dispose();
            refreshTimer = null;
            await SelfDestroy();
            return;
        }
    }

    public async ValueTask HeartBeatAsync(UserStatus status)
    {
        if (_userId == Guid.Empty)
        {
            logger.LogCritical("TRYING SET HEARTBEAT WITH NULL USERID, FIX ME");
            throw new ArgonDropConnectionException($"Trying set heartbeat with not active session");
        }

        _lastHeartbeatTime = DateTime.UtcNow;
        if (DateTime.UtcNow - (_lastDebouncedHeartbeatTime ?? new DateTime()) > TimeSpan.FromSeconds(30))
        {
            _lastDebouncedHeartbeatTime = DateTime.UtcNow;
            await presenceService.HeartbeatAsync(_userId, this.GetPrimaryKey());
        }

        if (_preferredStatus != status)
        {
            _preferredStatus = status;
            await UserSessionTickAsync(CancellationToken.None);
            await presenceService.HeartbeatAsync(_userId, this.GetPrimaryKey());
        }
    }

    public ValueTask EndRealtimeSession()
        => SelfDestroy();

    public async Task OnNextAsync(IArgonEvent item, StreamSequenceToken? token = null)
        => await userStream.Fire(item);

    public Task OnErrorAsync(Exception ex)
        => Task.CompletedTask;
}

public class ArgonDropConnectionException(string msg) : InvalidOperationException(msg);