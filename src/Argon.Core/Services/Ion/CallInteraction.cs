namespace Argon.Services.Ion;

using Core.Grains.Interfaces;

public class CallInteraction : ICallInteraction
{
    public async Task<IBeginCallResult> DingDongCreep(Guid creepId, CancellationToken ct = default)
    {
        var result = await this.GetGrain<ICallGrain>(Guid.CreateVersion7()).StartCallAsync(this.GetUserId(), creepId, ct);

        if (result.IsSuccess)
            return new SuccessDingDong(result.Value.CallerToken, result.Value.CallId);
        return new FailedDingDong(result.Error);
    }

    public async Task<IPickUpCallResult> PickUpCall(Guid callId, CancellationToken ct = default)
    {
        var result = await this.GetGrain<ICallGrain>(callId).AnswerAsync(this.GetUserId(), ct);

        if (string.IsNullOrEmpty(result.Error))
            return new SuccessPickUp(result.RoomToken!, callId);
        return new FailedPickUp(result.Error);
    }

    public async Task RejectCall(Guid callId, CancellationToken ct = default)
        => await this.GetGrain<ICallGrain>(callId).HangupAsync(this.GetUserId(), "rejected", ct);

    public async Task HangupCall(Guid callId, CancellationToken ct = default)
        => await this.GetGrain<ICallGrain>(callId).HangupAsync(this.GetUserId(), "complete", ct);
}