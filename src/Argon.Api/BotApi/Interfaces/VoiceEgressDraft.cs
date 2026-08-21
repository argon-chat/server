namespace Argon.Api.BotApi.Interfaces;

using Argon.Features.BotApi;
using Argon.Features.BotApi.Contracts;
using Argon.Core.Grains.Interfaces;
using Argon.Sfu;
using Microsoft.AspNetCore.Mvc;

[BotInterface("IVoiceEgress", 20260401)]
[BotDescription("Subscribe to individual voice tracks for audio egress. Verified bots only.")]
[BotRoute("POST", "/SubscribeTrack", RequestType = typeof(SubscribeTrackRequest), ResponseType = typeof(SubscribeTrackResponse), Description = "Subscribes to a user's audio track in a voice channel. Returns a LiveKit token and WebSocket URL for receiving the audio stream. The bot must be a verified bot and a member of the space.", Permission = "ConnectVoice", IsPrivileged = true)]
[BotRoute("DELETE", "/UnsubscribeTrack", RequestType = typeof(UnsubscribeTrackRequest), ResponseType = typeof(DeletedResponse), Description = "Unsubscribes from a previously subscribed audio track.", Permission = "ConnectVoice", IsPrivileged = true)]
[BotError("/SubscribeTrack", 403, "not_verified", "This endpoint requires a verified bot.")]
[BotError("/SubscribeTrack", 403, "not_a_member", "Bot is not a member of this space.")]
[BotError("/SubscribeTrack", 404, "channel_not_found", "Channel does not exist in this space.")]
[BotError("/SubscribeTrack", 400, "not_voice_channel", "Channel is not a voice channel.")]
[BotError("/UnsubscribeTrack", 403, "not_verified", "This endpoint requires a verified bot.")]
public sealed class VoiceEgressDraft(IGrainFactory grains, IOptions<CallKitOptions> callKit) : IBotInterface
{
    public sealed record SubscribeTrackRequest(
        Guid SpaceId,
        Guid ChannelId,
        Guid UserId);

    public sealed record SubscribeTrackResponse(
        string Token,
        string WsUrl,
        string RoomName,
        string TrackId);

    public sealed record UnsubscribeTrackRequest(
        string TrackId);

    public void MapRoutes(RouteGroupBuilder group)
    {
        group.AddEndpointFilter<BotOrleansPropagationFilter>();
        group.AddEndpointFilter<BotSpaceMembershipFilter>();
        group.RequireRateLimiting("Bot_IVoiceEgress");

        group.MapPost("/SubscribeTrack", async (SubscribeTrackRequest request, HttpContext ctx) =>
        {
            if (!ctx.GetBotIsVerified())
                return Results.Json(new BotApiError("not_verified", "This endpoint requires a verified bot."), statusCode: 403);

            var userId = ctx.GetBotAsUserId();

            var channels = await grains.GetGrain<ISpaceGrain>(request.SpaceId).GetChannels();
            var target = channels.FirstOrDefault(c => c.channel.channelId == request.ChannelId);

            if (target is null)
                return Results.NotFound(new BotApiError("channel_not_found", "Channel does not exist in this space."));

            if (target.channel.type != ChannelType.Voice)
                return Results.BadRequest(new BotApiError("not_voice_channel", "Channel is not a voice channel."));

            var roomId = ArgonRoomId.FromArgonChannel(request.SpaceId, request.ChannelId);

            var token = await grains.GetGrain<IVoiceControlGrain>(Guid.Empty)
                .IssueAuthorizationTokenAsync(
                    new ArgonUserId(userId),
                    roomId,
                    SfuPermissionKind.DefaultBot,
                    ctx.RequestAborted);

            var trackId = $"{roomId.ToRawRoomId()}:{request.UserId}";

            return Results.Ok(new SubscribeTrackResponse(
                token,
                callKit.Value.Sfu.PublicUrl,
                roomId.ToRawRoomId(),
                trackId));
        });

        group.MapDelete("/UnsubscribeTrack", async ([FromBody] UnsubscribeTrackRequest request, HttpContext ctx) =>
        {
            if (!ctx.GetBotIsVerified())
                return Results.Json(new BotApiError("not_verified", "This endpoint requires a verified bot."), statusCode: 403);

            // Track subscription lifecycle is managed by LiveKit — disconnecting the
            // WebSocket token is sufficient. This endpoint is a logical acknowledgement.
            return Results.Ok(new DeletedResponse(true));
        });
    }
}
