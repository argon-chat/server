namespace Argon.Api.BotApi.Interfaces;

using Argon.Features.BotApi;
using Argon.Features.BotApi.Contracts;
using Argon.Core.Grains.Interfaces;
using Argon.Sfu;

[BotInterface("IVoice", 1)]
[BotDescription("Get voice streaming tokens for audio ingress. Bots stream Opus audio directly to a WebSocket endpoint — no WebRTC needed.")]
[StableContract("31906fba806393345abaa611bacc5c0d7f86b3a06c9ef9023845bf1f89ca1b53")]
[BotRoute("POST", "/StreamToken", RequestType = typeof(VoiceStreamTokenRequest), ResponseType = typeof(VoiceStreamTokenResponse), Description = "Gets a LiveKit JWT token and WebSocket ingress URL for streaming audio into a voice channel. The bot must be a member of the space, and the channel must be a voice channel.", Permission = "ConnectVoice")]
[BotError("/StreamToken", 404, "channel_not_found", "Channel does not exist in this space.")]
[BotError("/StreamToken", 400, "not_voice_channel", "Channel is not a voice channel.")]
[BotError("/StreamToken", 403, "not_a_member", "Bot is not a member of this space.")]
public sealed class VoiceV1(IGrainFactory grains, IOptions<CallKitOptions> callKit) : IBotInterface
{
    public sealed record VoiceStreamTokenRequest(
        Guid SpaceId,
        Guid ChannelId);

    public sealed record VoiceStreamTokenResponse(
        string Token,
        string IngressUrl,
        string RoomName);

    public void MapRoutes(RouteGroupBuilder group)
    {
        group.AddEndpointFilter<BotOrleansPropagationFilter>();
        group.AddEndpointFilter<BotSpaceMembershipFilter>();
        group.RequireRateLimiting("Bot_IVoice");

        group.MapPost("/StreamToken", async (VoiceStreamTokenRequest request, HttpContext ctx) =>
        {
            var userId = ctx.GetBotAsUserId();

            // Verify channel exists and is a voice channel
            var channel = grains.GetGrain<IChannelGrain>(request.ChannelId);
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

            return Results.Ok(new VoiceStreamTokenResponse(
                token,
                callKit.Value.Sfu.AudioIngressUrl,
                roomId.ToRawRoomId()));
        });
    }
}
