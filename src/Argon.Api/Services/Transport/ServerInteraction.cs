namespace Argon.Services;

using Orleans.Runtime;

public class ServerInteraction(IGrainFactory grainFactory) : IServerInteraction
{
    public async Task CreateChannel(CreateChannelRequest request)
        => await grainFactory
           .GetGrain<IServerGrain>(request.serverId)
           .CreateChannel(new ChannelInput(request.name, new ChannelEntitlementOverwrite(), request.desc, request.kind), this.GetUser().id);

    public Task DeleteChannel(Guid serverId, Guid channelId)
        => grainFactory
           .GetGrain<IServerGrain>(serverId)
           .DeleteChannel(channelId, this.GetUser().id);

    public async Task<string> JoinToVoiceChannel(Guid serverId, Guid channelId)
    {
        var user = this.GetUser();
        var result = await grainFactory
           .GetGrain<IChannelGrain>(channelId)
           .Join(user.id);
        return result.Value.value;
    }

    public Task<List<RealtimeChannel>> GetChannels(Guid serverId)
        => grainFactory
           .GetGrain<IServerGrain>(serverId)
           .GetChannels();

    public Task<List<RealtimeServerMember>> GetMembers(Guid serverId)
        => grainFactory
           .GetGrain<IServerGrain>(serverId)
           .GetMembers();
}