namespace Argon.Api.BotApi.Interfaces;

using Argon.Features.BotApi;
using Argon.Features.BotApi.Contracts;
using Argon.Core.Grains.Interfaces;
using Argon.Sfu;

[BotInterface("ICalls", 20260401)]
[BotDescription("Receive and manage incoming calls. Verified bots only.")]
[BotRoute("POST", "/Accept", RequestType = typeof(AcceptCallRequest), ResponseType = typeof(AcceptCallResponse), Description = "Accepts an incoming call. Returns a LiveKit room token for joining the call audio. The bot must be the callee.", Permission = "ConnectVoice", IsPrivileged = true)]
[BotRoute("POST", "/Reject", RequestType = typeof(RejectCallRequest), ResponseType = typeof(RejectCallResponse), Description = "Rejects an incoming call with an optional reason.", Permission = "ConnectVoice", IsPrivileged = true)]
[BotRoute("GET", "/Ringing", ResponseType = typeof(RingingCallsResponse), Description = "Lists all currently ringing calls for the bot.", Permission = "ConnectVoice", IsPrivileged = true)]
[BotRoute("POST", "/SubscribeCallTrack", RequestType = typeof(SubscribeCallTrackRequest), ResponseType = typeof(SubscribeCallTrackResponse), Description = "Subscribes to the caller's audio track in an active call. Returns a token and WebSocket URL for receiving Opus audio frames. The caller may take a few seconds to connect after the call is accepted — retry or delay the subscription.", Permission = "ConnectVoice", IsPrivileged = true)]
[BotError("/Accept", 403, "not_verified", "This endpoint requires a verified bot.")]
[BotError("/Accept", 403, "not_callee", "This call is not directed at this bot.")]
[BotError("/Accept", 400, "not_ringing", "Call is not in ringing state.")]
[BotError("/Accept", 400, "accept_failed", "Failed to accept call.")]
[BotError("/Reject", 403, "not_verified", "This endpoint requires a verified bot.")]
[BotError("/Reject", 403, "not_callee", "This call is not directed at this bot.")]
[BotError("/Ringing", 403, "not_verified", "This endpoint requires a verified bot.")]
[BotError("/SubscribeCallTrack", 403, "not_verified", "This endpoint requires a verified bot.")]
[BotError("/SubscribeCallTrack", 403, "not_callee", "This call is not directed at this bot.")]
[BotError("/SubscribeCallTrack", 400, "not_accepted", "Call is not in accepted state.")]
public sealed class CallsDraft(IGrainFactory grains, IOptions<CallKitOptions> callKit) : IBotInterface
{
    public sealed record AcceptCallRequest(
        Guid CallId);

    public sealed record AcceptCallResponse(
        string Token,
        string RoomName);

    public sealed record RejectCallRequest(
        Guid   CallId,
        string? Reason);

    public sealed record RejectCallResponse(
        bool Rejected);

    public sealed record RingingCall(
        Guid CallId,
        Guid CallerId);

    public sealed record RingingCallsResponse(
        List<RingingCall> Calls);

    public sealed record SubscribeCallTrackRequest(
        Guid CallId);

    public sealed record SubscribeCallTrackResponse(
        string Token,
        string WsUrl,
        string RoomName,
        Guid   CallerId);

    public void MapRoutes(RouteGroupBuilder group)
    {
        group.AddEndpointFilter<BotOrleansPropagationFilter>();
        group.RequireRateLimiting("Bot_ICalls");

        group.MapPost("/Accept", async (AcceptCallRequest request, HttpContext ctx) =>
        {
            if (!ctx.GetBotIsVerified())
                return Results.Json(new BotApiError("not_verified", "This endpoint requires a verified bot."), statusCode: 403);

            var botUserId = ctx.GetBotAsUserId();
            var callGrain = grains.GetGrain<ICallGrain>(request.CallId);
            var state = await callGrain.GetStateAsync(ctx.RequestAborted);

            if (state.CalleeId != botUserId)
                return Results.Json(new BotApiError("not_callee", "This call is not directed at this bot."), statusCode: 403);

            if (state.Status != CallStatus.Ringing)
                return Results.BadRequest(new BotApiError("not_ringing", "Call is not in ringing state."));

            var result = await callGrain.AnswerAsync(botUserId, ctx.RequestAborted);

            if (!result.Success)
                return Results.BadRequest(new BotApiError("accept_failed", result.Error ?? "Failed to accept call."));

            return Results.Ok(new AcceptCallResponse(
                result.RoomToken!,
                state.RoomName));
        });

        group.MapPost("/Reject", async (RejectCallRequest request, HttpContext ctx) =>
        {
            if (!ctx.GetBotIsVerified())
                return Results.Json(new BotApiError("not_verified", "This endpoint requires a verified bot."), statusCode: 403);

            var botUserId = ctx.GetBotAsUserId();
            var callGrain = grains.GetGrain<ICallGrain>(request.CallId);
            var state = await callGrain.GetStateAsync(ctx.RequestAborted);

            if (state.CalleeId != botUserId)
                return Results.Json(new BotApiError("not_callee", "This call is not directed at this bot."), statusCode: 403);

            await callGrain.HangupAsync(botUserId, request.Reason ?? "rejected", ctx.RequestAborted);

            return Results.Ok(new RejectCallResponse(true));
        });

        group.MapGet("/Ringing", (HttpContext ctx) =>
        {
            if (!ctx.GetBotIsVerified())
                return Results.Json(new BotApiError("not_verified", "This endpoint requires a verified bot."), statusCode: 403);

            // Ringing calls are ephemeral — bots receive CALL_INCOMING events via SSE
            // and should track them locally. This endpoint returns an empty list as a
            // placeholder; a future version will query a persistent call registry.
            return Results.Ok(new RingingCallsResponse([]));
        });

        group.MapPost("/SubscribeCallTrack", async (SubscribeCallTrackRequest request, HttpContext ctx) =>
        {
            if (!ctx.GetBotIsVerified())
                return Results.Json(new BotApiError("not_verified", "This endpoint requires a verified bot."), statusCode: 403);

            var botUserId = ctx.GetBotAsUserId();
            var callGrain = grains.GetGrain<ICallGrain>(request.CallId);
            var state = await callGrain.GetStateAsync(ctx.RequestAborted);

            if (state.CalleeId != botUserId)
                return Results.Json(new BotApiError("not_callee", "This call is not directed at this bot."), statusCode: 403);

            if (state.Status != CallStatus.Accepted)
                return Results.BadRequest(new BotApiError("not_accepted", "Call is not in accepted state."));

            // The callee token already has CanSubscribe — reuse it for the egress connection.
            // Return the audio ingress URL (same service handles both publish and subscribe).
            return Results.Ok(new SubscribeCallTrackResponse(
                state.CalleeToken!,
                callKit.Value.Sfu.AudioIngressUrl,
                state.RoomName,
                state.CallerId));
        });
    }
}
