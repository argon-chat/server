namespace Argon.Services;

using Sfu;
using Shared.Servers;

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

    public async Task<Either<string, JoinToChannelError>> JoinToVoiceChannel(Guid serverId, Guid channelId)
    {
        var user = this.GetUser();
        var result = await grainFactory
           .GetGrain<IChannelGrain>(channelId)
           .Join(user.id, user.machineId);
        return result;
    }

    public async Task DisconnectFromVoiceChannel(Guid serverId, Guid channelId)
    {
        var user = this.GetUser();
        await grainFactory
           .GetGrain<IChannelGrain>(channelId)
           .Leave(user.id);
    }

    public Task<List<RealtimeChannel>> GetChannels(Guid serverId)
        => grainFactory
           .GetGrain<IServerGrain>(serverId)
           .GetChannels();

    public Task<List<RealtimeServerMember>> GetMembers(Guid serverId)
        => grainFactory
           .GetGrain<IServerGrain>(serverId)
           .GetMembers();

    public Task<List<InviteCodeEntity>> GetInviteCodes(Guid serverId)
        => grainFactory
           .GetGrain<IServerInvitesGrain>(serverId)
           .GetInviteCodes();

    public Task<InviteCode> CreateInviteCode(Guid serverId, TimeSpan expiration)
    {
        var user = this.GetUser();
        return grainFactory
           .GetGrain<IServerInvitesGrain>(serverId)
           .CreateInviteLinkAsync(user.id, expiration);
    }

    // TODO use access key
    public Task<User> PrefetchUser(Guid serverId, Guid userId)
        => grainFactory.GetGrain<IUserGrain>(userId).GetMe();
}