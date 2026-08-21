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

[BotError("/Accept", 403, "not_verified", "This endpoint requires a verified bot.")]
[BotError("/Accept", 403, "not_callee", "This call is not directed at this bot.")]
[BotError("/Accept", 400, "not_ringing", "Call is not in ringing state.")]
[BotError("/Accept", 400, "accept_failed", "Failed to accept call.")]
[BotError("/Reject", 403, "not_verified", "This endpoint requires a verified bot.")]
[BotError("/Reject", 403, "not_callee", "This call is not directed at this bot.")]
[BotError("/Ringing", 403, "not_verified", "This endpoint requires a verified bot.")]

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
        Guid   CallId,
        string? Reason);

    public sealed record RejectCallResponse(
        bool Rejected);

    public sealed record RingingCall(
        Guid CallId,
        Guid CallerId);

    public sealed record RingingCallsResponse(
        List<RingingCall> Calls);

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
                state.RoomName,
                state.CallerId,
                callKit.Value.Sfu.AudioIngressUrl));
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
    }
}
