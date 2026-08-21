namespace Argon.Core.Grains.Interfaces;

public interface ICallGrain : IGrainWithGuidKey
{
    Task<Either<CallInfo, CallFailedError>>     StartCallAsync(Guid callerId, Guid calleeId, CancellationToken ct = default);
    Task<AnswerResult> AnswerAsync(Guid userId, CancellationToken ct = default);
    Task               HangupAsync(Guid userId, string reason, CancellationToken ct = default);
    Task<CallInfo>     GetStateAsync(CancellationToken ct = default);
}

public interface ISipGrain : IGrainWithGuidKey
{
    Task<ServiceUssdResult> UssdExecute(Guid userId, string ussd, Guid corlId, CancellationToken ct = default);
    Task<IDialCheckResult>  BeginDialCheck(Guid userId, Guid phoneId, CancellationToken ct = default);
    Task<IBeginCallResult>  DialUp(Guid userId, Guid phoneId, Guid corlId, CancellationToken ct = default);
    Task                    HangupCall(Guid userId, Guid callId, CancellationToken ct = default);
}

public enum CallStatus
{
    None,
    Ringing,
    Accepted,
    Ended
}

public sealed class CallInfo
{
    public Guid       CallId   { get; set; }
    public Guid       CallerId { get; set; }
    public Guid       CalleeId { get; set; }
    public CallStatus Status   { get; set; }

    public string RoomName    { get; set; }
    public string CallerToken { get; set; }
    public string CalleeToken { get; set; }
}

public sealed class AnswerResult(bool success, string? error)
{
    public bool    Success { get; } = success;
    public string? Error   { get; } = error;
    public string? RoomToken { get; set; }
}