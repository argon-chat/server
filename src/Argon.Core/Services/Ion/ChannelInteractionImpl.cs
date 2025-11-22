namespace Argon.Services.Ion;

using Argon.Core.Grains.Interfaces;
using ion.runtime;

public class ChannelInteractionImpl : IChannelInteraction
{
    public async Task CreateChannel(Guid spaceId, Guid channelId, CreateChannelRequest request, CancellationToken ct = default)
        => await this
           .GetGrain<ISpaceGrain>(request.spaceId)
           .CreateChannel(new ChannelInput(request.name, request.desc, request.kind));

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
        var rtc = await this.GetGrain<IVoiceControlGrain>(Guid.NewGuid()).GetRtcEndpointAsync(ct);
        return new SuccessJoinVoice(rtc, result.Value);
    }

    public async Task<bool> KickMemberFromChannel(Guid spaceId, Guid channelId, Guid memberId, CancellationToken ct = default)
        => await this.GetGrain<IChannelGrain>(channelId).KickMemberFromChannel(memberId);
}