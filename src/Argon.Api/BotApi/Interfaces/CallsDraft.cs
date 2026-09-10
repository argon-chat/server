namespace Argon.Api.BotApi.Interfaces;

using Argon.Core.Grains.Interfaces;
using Argon.Features.BotApi;
using Argon.Sfu;

[BotInterface("ICalls", 20260401)]
[BotDescription("Receive and manage incoming calls. Verified bots only.")]
public sealed class CallsDraft(IGrainFactory grains, IOptions<CallKitOptions> callKit) : IBotInterface
{
    public sealed record AcceptCallRequest(
        Guid CallId);

    public sealed record AcceptCallResponse(
        string Token,
        string RoomName,
        Guid   CallerId,
        string AudioBaseUrl);

    public sealed record RejectCallRequest(
        Guid    CallId,
        string? Reason);

    public sealed record RejectCallResponse(
        bool Rejected);

    public sealed record RingingCall(
        Guid CallId,
        Guid CallerId);

    public sealed record RingingCallsResponse(
        List<RingingCall> Calls);

    private static readonly BotError NotCallee     = new(403, "not_callee", "This call is not directed at this bot.");
    private static readonly BotError NotRinging    = new(400, "not_ringing", "Call is not in ringing state.");
    private static readonly BotError AcceptFailed  = new(400, "accept_failed", "Failed to accept call.");

    public void MapRoutes(RouteGroupBuilder group)
    {
        group.AddEndpointFilter<BotOrleansPropagationFilter>();
        group.RequireRateLimiting("Bot_ICalls");

        group.Post<AcceptCallRequest, AcceptCallResponse>("/Accept")
           .Summary("Accepts an incoming call. Returns a LiveKit room token for joining the call audio. The bot must be the callee.")
           .Permission(ArgonEntitlement.Connect)
           .Privileged()
           .RequiresVerifiedBot()
           .Throws(NotCallee)
           .Throws(NotRinging)
           .Throws(AcceptFailed)
           .Handle(async (ctx, request) =>
            {
                var botUserId = ctx.GetBotAsUserId();
                var callGrain = grains.GetGrain<ICallGrain>(request.CallId);
                var state     = await callGrain.GetStateAsync(ctx.RequestAborted);

                if (state.CalleeId != botUserId)
                    throw NotCallee.Raise();

                if (state.Status != CallStatus.Ringing)
                    throw NotRinging.Raise();

                var result = await callGrain.AnswerAsync(botUserId, ctx.RequestAborted);

                if (!result.Success)
                    throw AcceptFailed.Raise(result.Error);

                return new AcceptCallResponse(
                    result.RoomToken!,
                    state.RoomName,
                    state.CallerId,
                    callKit.Value.Sfu.AudioIngressUrl);
            });

        group.Post<RejectCallRequest, RejectCallResponse>("/Reject")
           .Summary("Rejects an incoming call with an optional reason.")
           .Permission(ArgonEntitlement.Connect)
           .Privileged()
           .RequiresVerifiedBot()
           .Throws(NotCallee)
           .Handle(async (ctx, request) =>
            {
                var botUserId = ctx.GetBotAsUserId();
                var callGrain = grains.GetGrain<ICallGrain>(request.CallId);
                var state     = await callGrain.GetStateAsync(ctx.RequestAborted);

                if (state.CalleeId != botUserId)
                    throw NotCallee.Raise();

                await callGrain.HangupAsync(botUserId, request.Reason ?? "rejected", ctx.RequestAborted);

                return new RejectCallResponse(true);
            });

        group.Get<RingingCallsResponse>("/Ringing")
           .Summary("Lists all currently ringing calls for the bot.")
           .Permission(ArgonEntitlement.Connect)
           .Privileged()
           .RequiresVerifiedBot()
            // Ringing calls are ephemeral — bots receive CALL_INCOMING events over SSE and track
            // them locally. This returns an empty list until a persistent call registry exists.
           .Handle(_ => Task.FromResult(new RingingCallsResponse([])));
    }
}
