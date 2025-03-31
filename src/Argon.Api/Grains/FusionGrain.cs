namespace Argon.Grains;

using Features.Rpc;
using Orleans.Streams;
using static DeactivationReasonCode;

public class UserSessionGrain(IGrainFactory grainFactory, IClusterClient clusterClient, ILogger<IUserSessionGrain> logger)
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
        refreshTimer = this.RegisterGrainTimer(RefreshUserStatus, new GrainTimerCreationOptions
        {
            DueTime   = TimeSpan.FromSeconds(10),
            Period    = TimeSpan.FromSeconds(30),
            KeepAlive = true
        });

        var servers = await grainFactory
           .GetGrain<IUserGrain>(userId)
           .GetMyServersIds();
        foreach (var server in servers)
        {
            await grainFactory
               .GetGrain<IServerGrain>(server)
               .SetUserStatus(userId, preferredStatus ?? UserStatus.Online);
        }
    }

    private async Task RefreshUserStatus(CancellationToken arg)
    {
        var servers = await grainFactory
           .GetGrain<IUserGrain>(_userId)
           .GetMyServersIds();
        foreach (var server in servers)
            await grainFactory
               .GetGrain<IServerGrain>(server)
               .SetUserStatus(_userId, UserStatus.Online);
        this.DelayDeactivation(TimeSpan.FromMinutes(1));
        await userStream.Fire(new WelcomeCommander($"Outside temperature is {MathF.Round(Random.Shared.Next(-273_15, 45_00) / 100f)}\u00b0", UserStatus.Online,
            new UserNotificationSnapshot(servers.Select(x => new UserNotificationItem(x, 5)).ToList())));
    }

    public ValueTask EndRealtimeSession()
        => SelfDestroy();

    public async Task OnNextAsync(IArgonEvent item, StreamSequenceToken? token = null)
        => await userStream.Fire(item);

    public Task OnErrorAsync(Exception ex)
        => Task.CompletedTask;
}