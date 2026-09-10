namespace Argon.Api.BotApi.Interfaces;

using Argon.Core.Grains.Interfaces;
using Argon.Features.BotApi;
using Argon.Features.BotApi.Contracts;
using Argon.Sfu;

[BotInterface("IVoiceEgress", 20260401)]
[BotDescription("Subscribe to individual voice tracks for audio egress. Verified bots only.")]
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

    private static readonly BotError ChannelNotFound = new(404, "channel_not_found", "Channel does not exist in this space.");
    private static readonly BotError NotVoiceChannel = new(400, "not_voice_channel", "Channel is not a voice channel.");

    public void MapRoutes(RouteGroupBuilder group)
    {
        group.AddEndpointFilter<BotOrleansPropagationFilter>();
        group.RequireRateLimiting("Bot_IVoiceEgress");

        group.Post<SubscribeTrackRequest, SubscribeTrackResponse>("/SubscribeTrack")
           .Summary("Subscribes to a user's audio track in a voice channel. Returns a LiveKit token and WebSocket URL for receiving the audio stream.")
           .Permission(ArgonEntitlement.Connect)
           .Privileged()
           .RequiresVerifiedBot()
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

                return new SubscribeTrackResponse(
                    token,
                    callKit.Value.Sfu.PublicUrl,
                    roomId.ToRawRoomId(),
                    $"{roomId.ToRawRoomId()}:{request.UserId}");
            });

        group.Delete<UnsubscribeTrackRequest, DeletedResponse>("/UnsubscribeTrack")
           .FromBody()
           .Summary("Unsubscribes from a previously subscribed audio track.")
           .Permission(ArgonEntitlement.Connect)
           .Privileged()
           .RequiresVerifiedBot()
            // Track subscription lifecycle is managed by LiveKit — disconnecting the WebSocket
            // token is sufficient. This route is a logical acknowledgement.
           .Handle((_, _) => Task.FromResult(new DeletedResponse(true)));
    }
}
