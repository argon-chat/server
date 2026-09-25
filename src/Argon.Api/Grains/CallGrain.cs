namespace Argon.Grains;

using Argon.Core.Features.Logic;
using Argon.Core.Grains.Interfaces;
using Argon.Core.Services;
using Argon.Sfu;
using Livekit.Server.Sdk.Dotnet;
using Microsoft.EntityFrameworkCore;

public sealed class CallGrain(
    IDbContextFactory<ApplicationDbContext> context,
    IUserSessionDiscoveryService sessionDiscovery,
    IUserSessionNotifier notifier,
    ISfuAuthScope authScope,
    ISystemMessageService systemMessageService,
    ILogger<CallGrain> logger) : Grain, ICallGrain
{
    private readonly CallInfo        _state = new();
    private          IGrainTimer?    ringTimer;
    public static    TimeSpan        RingTimeout = TimeSpan.FromSeconds(45);
    private          DateTimeOffset? _callStartTime;
    private          bool            _hiddenFromCallee;

    public async Task<Either<CallInfo, CallFailedError>> StartCallAsync(Guid callerId, Guid calleeId, CancellationToken ct = default)
    {
        var callId = this.GetPrimaryKey();

        logger.LogInformation("Starting call {CallId}: {Caller} → {Callee}", callId, callerId, calleeId);

        // A caller the callee has blocked rings into the void, the same as calling someone offline.
        await using (var ctx = await context.CreateDbContextAsync(ct))
        {
            if (await ctx.UserBlocklist.AnyAsync(x => x.UserId == calleeId && x.BlockedId == callerId, ct))
            {
                _hiddenFromCallee = true;
                return await CallRouteToVoid(callerId, calleeId, ct);
            }
        }

        var sessions = await sessionDiscovery.GetUserSessionsAsync(calleeId, ct);
        if (sessions.Count == 0)
        {
            logger.LogWarning("No any found sessions for callee user, re-route call to void.");
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

        logger.LogInformation("Call {CallId} timed out", _state.CallId);

        if (!_hiddenFromCallee)
            await systemMessageService.SendCallTimeoutMessageAsync(_state.CallerId, _state.CalleeId, _state.CallId, ct);

        await HangupAsync(_state.CalleeId, "timeout", ct);
    }

    public async Task<AnswerResult> AnswerAsync(Guid userId, CancellationToken ct = default)
    {
        if (_state.Status != CallStatus.Ringing)
            return new AnswerResult(false, "not_ringing");

        if (userId != _state.CalleeId)
            return new AnswerResult(false, "not_callee");

        // The callee was never rung; answering by id would tell the blocked caller they got through.
        if (_hiddenFromCallee)
            return new AnswerResult(false, "not_ringing");

        if (ringTimer is not null)
        {
            ringTimer.Dispose();
            ringTimer = null;
        }

        _state.Status  = CallStatus.Accepted;
        _callStartTime = DateTimeOffset.UtcNow;

        logger.LogInformation("Call {CallId} accepted, sending system message", _state.CallId);

        _ = systemMessageService.SendCallStartedMessageAsync(_state.CallerId, _state.CalleeId, _state.CallId, ct);

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

        if (userId != _state.CallerId && userId != _state.CalleeId)
        {
            logger.LogWarning("User {UserId} is not in call {CallId} and cannot end it", userId, this.GetPrimaryKey());
            return;
        }

        var wasAccepted = _state.Status == CallStatus.Accepted;
        _state.Status = CallStatus.Ended;

        if (wasAccepted && _callStartTime.HasValue)
        {
            var duration = (int)(DateTimeOffset.UtcNow - _callStartTime.Value).TotalSeconds;
            logger.LogInformation("Call {CallId} ended after {Duration}s, sending system message", _state.CallId, duration);

            _ = systemMessageService.SendCallEndedMessageAsync(_state.CallerId, _state.CalleeId, _state.CallId, duration, ct);

            // Award XP for call time to both participants
            var callerStatsGrain = this.GrainFactory.GetGrain<IUserStatsGrain>(_state.CallerId);
            _ = callerStatsGrain.RecordVoiceTimeAsync(duration, Guid.Empty, Guid.Empty);

            // Only award XP to callee if not a special user (Echo, System, Void)
            if (_state.CalleeId != UserEntity.EchoUser && _state.CalleeId != UserEntity.SystemUser)
            {
                var calleeStatsGrain = this.GrainFactory.GetGrain<IUserStatsGrain>(_state.CalleeId);
                _ = calleeStatsGrain.RecordVoiceTimeAsync(duration, Guid.Empty, Guid.Empty);
            }
        }

        var sessionsCaller = await sessionDiscovery.GetUserSessionsAsync(_state.CallerId, ct);
        var sessionsCallee = await sessionDiscovery.GetUserSessionsAsync(_state.CalleeId, ct);

        await notifier.NotifySessionsAsync(
            sessionsCaller,
            new CallFinished(_state.CallerId, _state.CallId), ct);

        if (!_hiddenFromCallee)
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