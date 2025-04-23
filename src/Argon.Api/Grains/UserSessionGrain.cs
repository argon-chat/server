namespace Argon.Grains;

using Features.Logic;
using Features.Rpc;
using Orleans.Streams;
using static DeactivationReasonCode;

public class UserSessionGrain(
    IGrainFactory grainFactory, 
    IClusterClient clusterClient, 
    ILogger<IUserSessionGrain> logger,
    IUserPresenceService presenceService)
    : Grain, IUserSessionGrain, IAsyncObserver<IArgonEvent>
{
    private Guid _userId;
    private Guid _machineId;

    private IArgonStream<IArgonEvent> userStream;

    private IGrainTimer? refreshTimer;

    public async ValueTask SelfDestroy()
        => GrainContext.Deactivate(new(ApplicationRequested, "omae wa mou shindeiru"));

    public async override Task OnDeactivateAsync(DeactivationReason reason, CancellationToken cancellationToken)
    {
        if (reason.ReasonCode != ApplicationRequested)
            logger.LogCritical("Alert, deactivation user session grain is not graceful!, {reason}", reason);
        var servers = await grainFactory
           .GetGrain<IUserGrain>(_userId)
           .GetMyServersIds();
        foreach (var server in servers)
            await grainFactory
               .GetGrain<IServerGrain>(server)
               .SetUserStatus(_userId, UserStatus.Offline);

        refreshTimer?.Dispose();
    }

    public async ValueTask BeginRealtimeSession(Guid userId, Guid machineKey, UserStatus? preferredStatus = null)
    {
        this._userId    = userId;
        this._machineId = machineKey;

        userStream = await this.Streams().CreateServerStreamFor(_userId);
    }

    public async ValueTask HeartBeatAsync(UserStatus status)
    {
        await presenceService.HeartbeatAsync(_userId, _machineId);
        await RefreshUserStatus(status);
    }

    private async Task RefreshUserStatus(UserStatus status)
    {
        var servers = await grainFactory
           .GetGrain<IUserGrain>(_userId)
           .GetMyServersIds();
        foreach (var server in servers)
            await grainFactory
               .GetGrain<IServerGrain>(server)
               .SetUserStatus(_userId, status);
        this.DelayDeactivation(TimeSpan.FromMinutes(1));
    }

    public ValueTask EndRealtimeSession()
        => SelfDestroy();

    public async Task OnNextAsync(IArgonEvent item, StreamSequenceToken? token = null)
        => await userStream.Fire(item);

    public Task OnErrorAsync(Exception ex)
        => Task.CompletedTask;
}