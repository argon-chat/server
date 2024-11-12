namespace Argon.Api.Services;

using AutoMapper;
using Contracts;
using Entities;
using Grains.Interfaces;

public class ServerInteraction(IGrainFactory grainFactory, IFusionContext fusionContext, IMapper mapper) : IServerInteraction
{
    public async ValueTask<Guid> CreateChannel(Guid serverId, string name, ChannelType kind)
    {
        var result = await grainFactory.GetGrain<IServerGrain>(serverId).CreateChannel(new ChannelInput(name, ServerRole.User, "", kind));
        return result.Id;
    }

    public ValueTask DeleteChannel(Guid serverId, Guid channelId) => throw new NotImplementedException();

    public async ValueTask<ChannelJoinResponse> JoinToVoiceChannel(Guid serverId, Guid channelId)
    {
        var user   = await fusionContext.GetUserDataAsync();
        var result = await grainFactory.GetGrain<IChannelGrain>(channelId).Join(user.id);
        return new ChannelJoinResponse(result.Value.value);
    }
}