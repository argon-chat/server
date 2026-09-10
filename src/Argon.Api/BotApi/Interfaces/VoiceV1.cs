namespace Argon.Api.BotApi.Interfaces;

using Argon.Core.Grains.Interfaces;
using Argon.Features.BotApi;
using Argon.Sfu;

[BotInterface("IVoice", 1)]
[BotDescription("Get voice streaming tokens for audio ingress. Bots stream Opus audio directly to a WebSocket endpoint — no WebRTC needed.")]
public sealed class VoiceV1(IGrainFactory grains, IOptions<CallKitOptions> callKit) : IBotInterface
{
    public sealed record VoiceStreamTokenRequest(
        Guid SpaceId,
        Guid ChannelId);

    public sealed record VoiceStreamTokenResponse(
        string Token,
        string IngressUrl,
        string RoomName);

    private static readonly BotError ChannelNotFound = new(404, "channel_not_found", "Channel does not exist in this space.");
    private static readonly BotError NotVoiceChannel = new(400, "not_voice_channel", "Channel is not a voice channel.");

    public void MapRoutes(RouteGroupBuilder group)
    {
        group.AddEndpointFilter<BotOrleansPropagationFilter>();
        group.RequireRateLimiting("Bot_IVoice");

        group.Post<VoiceStreamTokenRequest, VoiceStreamTokenResponse>("/StreamToken")
           .Summary("Gets a LiveKit JWT token and WebSocket ingress URL for streaming audio into a voice channel. The bot must be a member of the space, and the channel must be a voice channel.")
           .Permission(ArgonEntitlement.Connect)
           .RequiresSpaceMembership()
           .Throws(ChannelNotFound)
           .Throws(NotVoiceChannel)
           .Handle(async (ctx, request) =>
            {
                var channels = await grains.GetGrain<ISpaceReadGrain>(request.SpaceId).GetChannels();
                var target   = channels.FirstOrDefault(c => c.channel.channelId == request.ChannelId)
                            ?? throw ChannelNotFound.Raise();

                if (target.channel.type != ChannelType.Voice)
                    throw NotVoiceChannel.Raise();

                var roomId = ArgonRoomId.FromArgonChannel(request.SpaceId, request.ChannelId);

                var token = await grains.GetGrain<IVoiceControlGrain>(Guid.Empty)
                   .IssueAuthorizationTokenAsync(
                        new ArgonUserId(ctx.GetBotAsUserId()),
                        roomId,
                        SfuPermissionKind.DefaultBot,
                        ctx.RequestAborted);

                return new VoiceStreamTokenResponse(
                    token,
                    callKit.Value.Sfu.AudioIngressUrl,
                    roomId.ToRawRoomId());
            });
    }
}
