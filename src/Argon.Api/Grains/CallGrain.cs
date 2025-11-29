namespace Argon.Grains;

using Argon.Core.Features.Logic;
using Argon.Core.Grains.Interfaces;
using Argon.Sfu;
using Livekit.Server.Sdk.Dotnet;
public sealed class CallGrain(
    IUserSessionDiscoveryService sessionDiscovery,
    IUserSessionNotifier notifier,
    ISfuAuthScope authScope,
    ILogger<CallGrain> logger) : Grain, ICallGrain
{
    private readonly CallInfo _state = new();

    public async Task<Either<CallInfo, CallFailedError>> StartCallAsync(Guid callerId, Guid calleeId, CancellationToken ct = default)
    {
        var callId = this.GetPrimaryKey();

        logger.LogInformation("Starting call {CallId}: {Caller} → {Callee}", callId, callerId, calleeId);

        if (!await sessionDiscovery.IsUserOnlineAsync(callerId, ct))
            return CallFailedError.CalleeOffline;

        var sessions = await sessionDiscovery.GetUserSessionsAsync(calleeId, ct);
        if (sessions.Count == 0)
            return CallFailedError.CalleeOffline;
        _state.CallId   = callId;
        _state.CallerId = callerId;
        _state.CalleeId = calleeId;

        _state.RoomName = $"call_{callId:N}";
        _state.Status   = CallStatus.Ringing;

        var grants = new VideoGrants()
        {
            CanPublish   = true,
            CanSubscribe = true,
            Room         = _state.RoomName,
            RoomJoin     = true,
            RoomCreate   = true
        };


        _state.CallerToken = authScope.GenerateToken(callerId.ToString(), _state.RoomName, callerId.ToString(), grants, TimeSpan.FromHours(1));
        _state.CalleeToken = authScope.GenerateToken(calleeId.ToString(), _state.RoomName, calleeId.ToString(), grants, TimeSpan.FromHours(1));

        await notifier.NotifySessionsAsync(
            sessions,
            new CallIncoming(calleeId, callId, callerId), ct);

        return _state;
    }

    public async Task<AnswerResult> AnswerAsync(Guid userId, CancellationToken ct = default)
    {
        if (_state.Status != CallStatus.Ringing)
            return new AnswerResult(false, "not_ringing");

        if (userId != _state.CalleeId)
            return new AnswerResult(false, "not_callee");

        _state.Status = CallStatus.Accepted;

        var callerSessions = await sessionDiscovery.GetUserSessionsAsync(_state.CallerId, ct);
        await notifier.NotifySessionsAsync(
            callerSessions,
            new CallAccepted(_state.CallerId, _state.CallId, userId), ct);

        return new AnswerResult(true, null)
        {
            RoomToken = _state.CalleeToken
        };
    }

    public async Task HangupAsync(Guid userId, string reason, CancellationToken ct = default)
    {
        if (_state.Status == CallStatus.Ended)
            return;

        _state.Status = CallStatus.Ended;


        var sessionsCaller = await sessionDiscovery.GetUserSessionsAsync(_state.CallerId, ct);
        var sessionsCallee = await sessionDiscovery.GetUserSessionsAsync(_state.CalleeId, ct);

        await notifier.NotifySessionsAsync(
            sessionsCaller,
            new CallFinished(_state.CallerId, _state.CallId), ct);

        await notifier.NotifySessionsAsync(
            sessionsCallee,
            new CallFinished(_state.CalleeId, _state.CallId), ct);
    }

    public Task<CallInfo> GetStateAsync(CancellationToken ct = default)
        => Task.FromResult(_state);
}