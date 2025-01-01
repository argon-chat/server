namespace Argon.Grains;

using Features.Jwt;
using Features.Rpc;
using R3;
using static DeactivationReasonCode;

public class FusionGrain(IGrainFactory grainFactory) : Grain, IFusionSessionGrain
{
    private Guid _userId;
    private Guid _machineId;

    private IArgonStream<IArgonEvent> userStream;

    public async ValueTask SelfDestroy()
    {
        var servers = await grainFactory
           .GetGrain<IUserGrain>(_userId)
           .GetMyServersIds();
        foreach (var server in servers)
            await grainFactory
               .GetGrain<IServerGrain>(server)
               .SetUserStatus(_userId, UserStatus.Offline);
        _userId    = default;
        _machineId = default;
        GrainContext.Deactivate(new(ApplicationRequested, "omae wa mou shindeiru"));
    }

    public async ValueTask BeginRealtimeSession(Guid userId, Guid machineKey, UserStatus? preferredStatus = null)
    {
        this._userId    = userId;
        this._machineId = machineKey;

        userStream = await this.Streams().CreateServerStreamFor(_userId);

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

        await userStream.Fire(new WelcomCommander($"Outside temperature is {MathF.Round(Random.Shared.Next(-273_15, 5500_00) / 100f)}\u00b0"));
    }

    public ValueTask EndRealtimeSession()
        => SelfDestroy();

    public ValueTask<bool> HasSessionActive()
        => new(_userId != default);

    public ValueTask<TokenUserData> GetTokenUserData()
        => new(new TokenUserData(_userId, _machineId));
}