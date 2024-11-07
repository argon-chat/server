namespace Argon.Api.Grains;

using Extensions;
using Interfaces;
using Microsoft.IdentityModel.Tokens;
using Orleans.Streams;
using R3;
using static DeactivationReasonCode;
using static FusionGrainEventKind;

public class FusionGrain : Grain, IFusionSessionGrain
{
    private IAsyncStream<FusionGrainEventKind> _stream = null!;
    private DateTimeOffset _latestSignalTime = DateTimeOffset.UtcNow;
    private DisposableBag disposableBag;
    private Guid _userId;

    public async ValueTask SelfDestroy()
        => GrainContext.Deactivate(new(ApplicationRequested, "omae wa mou shindeiru"));


    public override Task OnActivateAsync(CancellationToken cancellationToken)
    {
        var streamProvider = this.GetStreamProvider(IFusionSessionGrain.StreamProviderId);

        var streamId = StreamId.Create(IFusionSessionGrain.SelfNs, this.GetPrimaryKey());

        _stream = streamProvider.GetStream<FusionGrainEventKind>(
            streamId);

        return base.OnActivateAsync(cancellationToken);
    }

    private Task OnValidateActiveAsync(CancellationToken arg)
        => _latestSignalTime.WhenAsync(x => DateTimeOffset.UtcNow - x > TimeSpan.FromMinutes(1), SelfDestroy);

    public async override Task OnDeactivateAsync(DeactivationReason reason, CancellationToken cancellationToken)
    {
        disposableBag.Dispose();
        if (reason.ReasonCode == Migrating)
            await _stream.OnNextAsync(CONNECTION_REQUIRED_MIGRATE);
        else
            await _stream.OnNextAsync(CONNECTION_DESTROYED);
    }

    public ValueTask BeginRealtimeSession(Guid userId, Guid machineKey)
    {
        this.RegisterGrainTimer(OnValidateActiveAsync, TimeSpan.FromSeconds(30), TimeSpan.FromSeconds(30))
           .AddTo(ref disposableBag);
        this._userId = userId;
        // todo notify latest access by machineKey
        return ValueTask.CompletedTask;
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
}


public enum FusionGrainEventKind
{
    CONNECTION_ESTABLISHED,
    CONNECTION_REQUIRED_MIGRATE,
    CONNECTION_DESTROYED
}