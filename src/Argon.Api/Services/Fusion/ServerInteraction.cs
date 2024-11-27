namespace Argon.Api.Services;

using Grains.Interfaces;
using Contracts;
using Contracts.Models;

public class ServerInteraction(IGrainFactory grainFactory, IFusionContext fusionContext) : IServerInteraction
{
    public async Task<CreateChannelResponse> CreateChannel(CreateChannelRequest request)
    {
        var user = await fusionContext.GetUserDataAsync();
        var result = await grainFactory
           .GetGrain<IServerGrain>(request.serverId)
           .CreateChannel(new ChannelInput(request.name, new ChannelEntitlementOverwrite(), request.desc, request.kind), user.id);
        return new CreateChannelResponse(request.serverId, result.Id);
    }

    public Task DeleteChannel(DeleteChannelRequest request)
        => throw new NotImplementedException();

    public async Task<ChannelJoinResponse> JoinToVoiceChannel(JoinToVoiceChannelRequest request)
    {
        var user = await fusionContext.GetUserDataAsync();
        var result = await grainFactory
           .GetGrain<IChannelGrain>(request.channelId)
           .Join(user.id);
        return new ChannelJoinResponse(result.Value.value);
    }
}