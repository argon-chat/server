namespace Argon.Services.Ion;

using Core.Grains.Interfaces;

public class CallInteraction : ICallInteraction
{
    private ISipGrain SipGrain => this.GetGrain<ISipGrain>(Guid.CreateVersion7());

    public async Task<IBeginCallResult> DingDongCreep(Guid creepId, CancellationToken ct = default)
    {
        var result = await this.GetGrain<ICallGrain>(Guid.CreateVersion7()).StartCallAsync(this.GetUserId(), creepId, ct);

        if (!result.IsSuccess) 
            return new FailedDingDong(result.Error);

        var rtc = await this.GetGrain<IVoiceControlGrain>(Guid.Empty).GetRtcEndpointAsync(ct);
        return new SuccessDingDong(result.Value.CallerToken, result.Value.CallId, rtc);
    }

    public async Task<IPickUpCallResult> PickUpCall(Guid callId, CancellationToken ct = default)
    {
        var result = await this.GetGrain<ICallGrain>(callId).AnswerAsync(this.GetUserId(), ct);

        if (!string.IsNullOrEmpty(result.Error))
            return new FailedPickUp(result.Error);
        var rtc = await this.GetGrain<IVoiceControlGrain>(Guid.Empty).GetRtcEndpointAsync(ct);
        return new SuccessPickUp(result.RoomToken ?? "", callId, rtc);
    }

    public async Task RejectCall(Guid callId, CancellationToken ct = default)
        => await this.GetGrain<ICallGrain>(callId).HangupAsync(this.GetUserId(), "rejected", ct);

    public async Task HangupCall(Guid callId, CancellationToken ct = default)
        => await this.GetGrain<ICallGrain>(callId).HangupAsync(this.GetUserId(), "complete", ct);

    public async Task<ServiceUssdResult> UssdExecute(string ussd, Guid corlId, CancellationToken ct = default)
        => await SipGrain.UssdExecute(this.GetUserId(), ussd, corlId, ct);

    public async Task<IDialCheckResult> BeginDialCheck(Guid phoneId, CancellationToken ct = default)
        => await SipGrain.BeginDialCheck(this.GetUserId(), phoneId, ct);

    public async Task<IBeginCallResult> DialUp(Guid phoneId, Guid corlId, CancellationToken ct = default)
        => await SipGrain.DialUp(this.GetUserId(), phoneId, corlId, ct);
}