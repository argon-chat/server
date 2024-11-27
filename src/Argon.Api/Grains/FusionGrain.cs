namespace Argon.Api.Grains;

using Contracts;
using Extensions;
using Features.Jwt;
using Features.Rpc;
using Interfaces;
using R3;
using static DeactivationReasonCode;

public class FusionGrain(IGrainFactory grainFactory) : Grain, IFusionSessionGrain
{
    private DateTimeOffset _latestSignalTime = DateTimeOffset.UtcNow;
    private DisposableBag  disposableBag;

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

    private Task OnValidateActiveAsync(CancellationToken arg)
        => _latestSignalTime.WhenAsync(x => DateTimeOffset.UtcNow - x > TimeSpan.FromMinutes(1), SelfDestroy);

    public async override Task OnDeactivateAsync(DeactivationReason reason, CancellationToken cancellationToken)
        => disposableBag.Dispose();

    public async ValueTask BeginRealtimeSession(Guid userId, Guid machineKey, UserStatus? preferredStatus = null)
    {
        this.RegisterGrainTimer(OnValidateActiveAsync, TimeSpan.FromMinutes(2), TimeSpan.FromMinutes(2))
           .AddTo(ref disposableBag);
        this._userId    = userId;
        this._machineId = machineKey;

        userStream      = await this.Streams().CreateServerStreamFor(_userId);

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
    }

    public ValueTask EndRealtimeSession()
        => SelfDestroy();

    public ValueTask<bool> HasSessionActive()
        => new(_userId != default);

    public ValueTask Signal()
    {
        _latestSignalTime = DateTimeOffset.UtcNow;
        return ValueTask.CompletedTask;
    }

    public ValueTask<TokenUserData> GetTokenUserData()
        => new(new TokenUserData(_userId, _machineId));
}