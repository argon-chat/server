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
    private readonly        CallInfo     _state = new();
    private                 IGrainTimer? ringTimer;
    private static readonly TimeSpan     RingTimeout = TimeSpan.FromSeconds(45);

    public async Task<Either<CallInfo, CallFailedError>> StartCallAsync(Guid callerId, Guid calleeId, CancellationToken ct = default)
    {
        var callId = this.GetPrimaryKey();

        logger.LogInformation("Starting call {CallId}: {Caller} → {Callee}", callId, callerId, calleeId);

        if (calleeId == UserEntity.EchoUser)
        {
            // detected call to Echo, re-route to echo
            return await RouteToEchoCall(callerId, ct);
        }

        var sessions = await sessionDiscovery.GetUserSessionsAsync(calleeId, ct);
        if (sessions.Count == 0)
        {
            // no any found sessions for callee user, re-route call to void
            return await CallRouteToVoid(callerId, calleeId, ct);
        }
        _state.CallId   = callId;
        _state.CallerId = callerId;
        _state.CalleeId = calleeId;

        _state.RoomName = $"call_{callId:N}";
        _state.Status   = CallStatus.Ringing;

        var grants = new VideoGrants()
        {
            CanPublish           = true,
            CanSubscribe         = true,
            Room                 = _state.RoomName,
            RoomJoin             = true,
            RoomCreate           = true,
            CanPublishData       = true,
            CanSubscribeMetrics  = true,
            CanUpdateOwnMetadata = true,
        };


        _state.CallerToken = authScope.GenerateToken(callerId.ToString(), _state.RoomName, callerId.ToString(), grants, TimeSpan.FromHours(1));
        _state.CalleeToken = authScope.GenerateToken(calleeId.ToString(), _state.RoomName, calleeId.ToString(), grants, TimeSpan.FromHours(1));

        await notifier.NotifySessionsAsync(
            sessions,
            new CallIncoming(calleeId, callId, callerId), ct);

        ringTimer = this.RegisterGrainTimer(RingTimeoutReached, new GrainTimerCreationOptions(RingTimeout, Timeout.InfiniteTimeSpan));

        return _state;
    }

    private async Task<Either<CallInfo, CallFailedError>> RouteToEchoCall(Guid callerId, CancellationToken ct)
    {
        var callId = this.GetPrimaryKey();
        _state.CallId   = callId;
        _state.CallerId = callerId;
        _state.CalleeId = UserEntity.EchoUser;

        _state.RoomName = $"call_{callId:N}";
        _state.Status   = CallStatus.Ringing;

        var grants = new VideoGrants()
        {
            CanPublish           = true,
            CanSubscribe         = true,
            Room                 = _state.RoomName,
            RoomJoin             = true,
            RoomCreate           = true,
            CanPublishData       = true,
            CanSubscribeMetrics  = true,
            CanUpdateOwnMetadata = true,
        };


        _state.CallerToken = authScope.GenerateToken(callerId.ToString(), _state.RoomName, callerId.ToString(), grants, TimeSpan.FromHours(1));
        _state.CalleeToken = authScope.GenerateToken(UserEntity.EchoUser.ToString(), _state.RoomName, UserEntity.EchoUser.ToString(), grants, TimeSpan.FromHours(1));

        ringTimer = this.RegisterGrainTimer(RingTimeoutReached, new GrainTimerCreationOptions(RingTimeout, Timeout.InfiniteTimeSpan));

        _ = this.GrainFactory.GetGrain<IEchoSessionGrain>(this.GetPrimaryKey())
           .RequestJoinEchoAsync(new EchoJoinRequest(_state.RoomName, _state.CalleeToken, "girl_echo_01", "https://rts.argon.gl", callerId), ct);

        _state.Status = CallStatus.Accepted;
        return _state;
    }

    private async Task<Either<CallInfo, CallFailedError>> CallRouteToVoid(Guid callerId, Guid calleeId, CancellationToken ct = default)
    {
        var callId = this.GetPrimaryKey();
        
        _state.CallId   = callId;
        _state.CallerId = callerId;
        _state.CalleeId = calleeId;

        _state.RoomName = $"call_{callId:N}";
        _state.Status   = CallStatus.Ringing;

        var grants = new VideoGrants()
        {
            CanPublish           = true,
            CanSubscribe         = true,
            Room                 = _state.RoomName,
            RoomJoin             = true,
            RoomCreate           = true,
            CanPublishData       = true,
            CanSubscribeMetrics  = true,
            CanUpdateOwnMetadata = true,
        };


        _state.CallerToken = authScope.GenerateToken(callerId.ToString(), _state.RoomName, callerId.ToString(), grants, TimeSpan.FromHours(1));

        ringTimer = this.RegisterGrainTimer(RingTimeoutReached, new GrainTimerCreationOptions(RingTimeout, Timeout.InfiniteTimeSpan));

        return _state;
    }


    private async Task RingTimeoutReached(CancellationToken ct = default)
    {
        if (_state.Status != CallStatus.Ringing)
            return;

        await HangupAsync(_state.CalleeId, "timeout", ct);
    }

    public async Task<AnswerResult> AnswerAsync(Guid userId, CancellationToken ct = default)
    {
        if (_state.Status != CallStatus.Ringing)
            return new AnswerResult(false, "not_ringing");

        if (userId != _state.CalleeId)
            return new AnswerResult(false, "not_callee");

        if (ringTimer is not null)
        {
            ringTimer.Dispose();
            ringTimer = null;
        }

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


        if (ringTimer is not null)
        {
            ringTimer.Dispose();
            ringTimer = null;
        }
    }

    public Task<CallInfo> GetStateAsync(CancellationToken ct = default)
        => Task.FromResult(_state);
}