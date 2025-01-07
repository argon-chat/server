namespace Argon.Grains;

using Features.Jwt;
using Features.Rpc;
using static DeactivationReasonCode;

public class FusionGrain(IGrainFactory grainFactory, IClusterClient clusterClient) : Grain, IFusionSessionGrain
{
    private Guid _userId;
    private Guid _machineId;
    private Guid _activeChannelId;

    private IArgonStream<IArgonEvent> userStream;

    private IGrainTimer? refreshTimer;

    public async ValueTask SelfDestroy()
    {
        if (refreshTimer is not null)
            refreshTimer.Dispose();
        var servers = await grainFactory
           .GetGrain<IUserGrain>(_userId)
           .GetMyServersIds();
        foreach (var server in servers)
            await grainFactory
               .GetGrain<IServerGrain>(server)
               .SetUserStatus(_userId, UserStatus.Offline);
        if (_activeChannelId != default)
            await grainFactory
               .GetGrain<IChannelGrain>(_activeChannelId)
               .Leave(_userId);
        _userId    = default;
        _machineId = default;
        GrainContext.Deactivate(new(ApplicationRequested, "omae wa mou shindeiru"));
    }

    public async ValueTask BeginRealtimeSession(Guid userId, Guid machineKey, UserStatus? preferredStatus = null)
    {
        this._userId    = userId;
        this._machineId = machineKey;

        userStream = await this.Streams().CreateServerStreamFor(_userId);
        refreshTimer = this.RegisterGrainTimer(RefreshUserStatus, TimeSpan.FromSeconds(10), TimeSpan.FromSeconds(10));

        await grainFactory
           .GetGrain<IUserMachineSessions>(userId)
           .IndicateLastActive(machineKey);
        var servers = await grainFactory
           .GetGrain<IUserGrain>(userId)
           .GetMyServersIds();
        foreach (var server in servers)
            await grainFactory
               .GetGrain<IServerGrain>(server)
               .SetUserStatus(userId, preferredStatus ?? UserStatus.Online);

        await userStream.Fire(new WelcomeCommander($"Outside temperature is {MathF.Round(Random.Shared.Next(-273_15, 45_00) / 100f)}\u00b0",
            preferredStatus ?? UserStatus.Online,
            new UserNotificationSnapshot(servers.Select(x => new UserNotificationItem(x, 5)).ToList())));
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
    }

    public ValueTask EndRealtimeSession()
        => SelfDestroy();

    public ValueTask<bool> HasSessionActive()
        => new(_userId != default);

    public ValueTask<TokenUserData> GetTokenUserData()
        => new(new TokenUserData(_userId, _machineId));

    public ValueTask SetActiveChannelConnection(Guid channelId)
    {
        _activeChannelId = channelId;
        return ValueTask.CompletedTask;
    }
}