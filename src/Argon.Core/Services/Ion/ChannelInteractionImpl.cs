namespace Argon.Services.Ion;

using Argon.Api.Features.Utils;
using Argon.Core.Grains.Interfaces;
using Argon.Sfu;
using ion.runtime;
using Livekit.Server.Sdk.Dotnet;

public class ChannelInteractionImpl(IngressServiceClient ingressService, ILogger<IChannelInteraction> logger) : IChannelInteraction
{
    public async Task CreateChannelGroup(Guid spaceId, Guid channelId, string name, string? description, CancellationToken ct = default)
        => await this
           .GetGrain<ISpaceGrain>(spaceId)
           .CreateChannelGroup(name, description);
        
    public async Task MoveChannelGroup(Guid spaceId, Guid groupId, Guid? afterGroupId, Guid? beforeGroupId, CancellationToken ct = default)
        => await this
           .GetGrain<ISpaceGrain>(spaceId)
           .MoveChannelGroup(groupId, afterGroupId, beforeGroupId);

    public async Task DeleteChannelGroup(Guid spaceId, Guid channelId, Guid groupId, bool deleteChannels, CancellationToken ct = default)
        => await this
           .GetGrain<ISpaceGrain>(spaceId)
           .DeleteChannelGroup(groupId, deleteChannels);

    public async Task CreateChannel(Guid spaceId, Guid channelId, CreateChannelRequest request, CancellationToken ct = default)
        => await this
           .GetGrain<ISpaceGrain>(request.spaceId)
           .CreateChannel(new ChannelInput(request.name, request.desc, request.kind), request.groupId);

    public async Task MoveChannel(Guid spaceId, Guid channelId, Guid? targetGroupId, Guid? afterChannelId, Guid? beforeChannelId, CancellationToken ct = default)
        => await this
           .GetGrain<ISpaceGrain>(spaceId)
           .MoveChannel(channelId, targetGroupId, afterChannelId, beforeChannelId);

    public async Task DeleteChannel(Guid spaceId, Guid channelId, CancellationToken ct = default)
        => await this
           .GetGrain<ISpaceGrain>(spaceId)
           .DeleteChannel(channelId);

    public async Task<IonArray<RealtimeChannel>> GetChannels(Guid spaceId, Guid channelId, CancellationToken ct = default)
        => new(await this.GetGrain<ISpaceGrain>(spaceId)
           .GetChannels());

    public async Task<IonArray<ArgonMessage>> QueryMessages(Guid spaceId, Guid channelId, long? from, int limit, CancellationToken ct = default)
    {
        var result = await this
           .GetGrain<IChannelGrain>(channelId)
           .QueryMessages(from, limit);

        return result.Select(x => x.ToDto()).ToList();
    }

    public async Task<long> SendMessage(Guid spaceId, Guid channelId, string text, IonArray<IMessageEntity> entities, long randomId,
        long? replyTo, CancellationToken ct = default)
        => await this
           .GetGrain<IChannelGrain>(channelId)
           .SendMessage(text, entities.Values.ToList(), randomId, replyTo);

    public async Task DisconnectFromVoiceChannel(Guid spaceId, Guid channelId, CancellationToken ct = default)
        => await this
           .GetGrain<IChannelGrain>(channelId)
           .Leave(this.GetUserId());

    public async Task<IInterlinkResult> Interlink(Guid spaceId, Guid channelId, CancellationToken ct = default)
    {
        var result = await this.GetGrain<IChannelGrain>(channelId).Join();

        if (!result.IsSuccess) 
            return new FailedJoinVoice(result.Error);
        var rtc = await this.GetGrain<IVoiceControlGrain>(Guid.Empty).GetRtcEndpointAsync(ct);
        return new SuccessJoinVoice(rtc, result.Value);
    }

    public async Task<IInterlinkStreamResult> InterlinkStream(Guid spaceId, Guid channelId, int density, CancellationToken ct = default)
    {
        // TODO check scoping
        var ingressUrl = "";
        var ingressKey = "";
        try
        {
            var ingressResult = await ingressService.CreateIngress(new CreateIngressRequest()
            {
                Name                = $"Streaming.{spaceId}.{channelId}.{density}.{this.GetUserId()}",
                Enabled             = true,
                InputType           = IngressInput.WhipInput,
                RoomName            = ArgonRoomId.FromArgonChannel(spaceId, channelId).ToRawRoomId(),
                ParticipantName     = this.GetUserId().ToString(),
                ParticipantIdentity = this.GetUserId().ToString()
            });

            if (ingressResult is null)
            {
                return new FailedStartStream(StartStreamError.BAD_PARAMS);
            }

            ingressUrl = ingressResult.Url;
            ingressKey = ingressResult.StreamKey;
            // TODO enqueue to gc ingress
        }
        catch (Exception e)
        {
            logger.LogError(e, "failed create ingress");
            return new FailedStartStream(StartStreamError.INTERNAL_ERROR);
        }

        var rtc = await this.GetGrain<IVoiceControlGrain>(Guid.Empty).GetRtcEndpointAsync(ct);
        return new SuccessStartStream(rtc, ingressKey, ingressUrl);
    }

    public async Task<bool> KickMemberFromChannel(Guid spaceId, Guid channelId, Guid memberId, CancellationToken ct = default)
        => await this.GetGrain<IChannelGrain>(channelId).KickMemberFromChannel(memberId);

    public Task<bool> BeginRecord(Guid spaceId, Guid channelId, CancellationToken ct = default)
        => this.GetGrain<IChannelGrain>(channelId).BeginRecord(ct);

    public Task<bool> StopRecord(Guid spaceId, Guid channelId, CancellationToken ct = default)
        => this.GetGrain<IChannelGrain>(channelId).StopRecord(ct);
}