namespace Argon.Services;

using Shared.Servers;

public class ServerInteraction : IServerInteraction
{
    public async Task CreateChannel(CreateChannelRequest request)
        => await this.GetGrainFactory()
           .GetGrain<IServerGrain>(request.serverId)
           .CreateChannel(new ChannelInput(request.name, new ChannelEntitlementOverwrite(), request.desc, request.kind), this.GetUser().id);

    public Task DeleteChannel(Guid serverId, Guid channelId)
        => this.GetGrainFactory()
           .GetGrain<IServerGrain>(serverId)
           .DeleteChannel(channelId, this.GetUser().id);

    public async Task<Either<string, JoinToChannelError>> JoinToVoiceChannel(Guid serverId, Guid channelId)
    {
        var user = this.GetUser();
        var result = await this.GetGrainFactory()
           .GetGrain<IChannelGrain>(channelId)
           .Join(user.id, user.machineId);
        return result;
    }

    public async Task DisconnectFromVoiceChannel(Guid serverId, Guid channelId)
    {
        var user = this.GetUser();
        await this.GetGrainFactory()
           .GetGrain<IChannelGrain>(channelId)
           .Leave(user.id);
    }

    public Task<List<RealtimeChannel>> GetChannels(Guid serverId)
        => this.GetGrainFactory()
           .GetGrain<IServerGrain>(serverId)
           .GetChannels();

    public Task<List<RealtimeServerMember>> GetMembers(Guid serverId)
        => this.GetGrainFactory()
           .GetGrain<IServerGrain>(serverId)
           .GetMembers();

    public Task<List<InviteCodeEntity>> GetInviteCodes(Guid serverId)
        => this.GetGrainFactory()
           .GetGrain<IServerInvitesGrain>(serverId)
           .GetInviteCodes();

    public Task<InviteCode> CreateInviteCode(Guid serverId, TimeSpan expiration)
    {
        var user = this.GetUser();
        return this.GetGrainFactory()
           .GetGrain<IServerInvitesGrain>(serverId)
           .CreateInviteLinkAsync(user.id, expiration);
    }

    public Task SendMessage(Guid channelId, string text, List<MessageEntity> entities)
    {
        var user = this.GetUser();

        return this.GetGrainFactory()
           .GetGrain<IChannelGrain>(channelId)
           .SendMessage(user.id, text, entities);
    }

    public Task<List<ArgonMessage>> GetMessages(Guid channelId, int count, int offset) => this.GetGrainFactory()
       .GetGrain<IChannelGrain>(channelId)
       .GetMessages(count, offset);

    // TODO use access key
    public Task<User> PrefetchUser(Guid serverId, Guid userId)
        => this.GetGrainFactory().GetGrain<IUserGrain>(userId).GetMe();
}