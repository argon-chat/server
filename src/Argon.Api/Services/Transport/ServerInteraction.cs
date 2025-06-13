namespace Argon.Services;

using Grpc.Core;
using Shared.Servers;

public class ServerInteraction : IServerInteraction
{
    public async Task CreateChannel(CreateChannelRequest request)
        => await this.GetGrainFactory()
           .GetGrain<IServerGrain>(request.serverId)
           .CreateChannel(new ChannelInput(request.name, new ChannelEntitlementOverwrite(), request.desc, request.kind));

    public Task DeleteChannel(Guid serverId, Guid channelId)
        => this.GetGrainFactory()
           .GetGrain<IServerGrain>(serverId)
           .DeleteChannel(channelId);

    public Task<RealtimeServerMember> GetMember(Guid serverId, Guid userId)
        => this.GetGrainFactory()
           .GetGrain<IServerGrain>(serverId)
           .GetMember(userId);

    public async Task<Either<string, JoinToChannelError>> JoinToVoiceChannel(Guid serverId, Guid channelId)
        => await this.GetGrainFactory()
           .GetGrain<IChannelGrain>(channelId)
           .Join();

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

    public Task SendMessage(Guid channelId, string text, List<MessageEntity> entities, ulong? replyTo)
        => this.GetGrainFactory()
           .GetGrain<IChannelGrain>(channelId)
           .SendMessage(text, entities, replyTo);

    public Task<List<ArgonMessageDto>> GetMessages(Guid channelId, int count, int offset)
        => this.GetGrainFactory()
           .GetGrain<IChannelGrain>(channelId)
           .GetMessages(count, offset)
           .ToDto();

    // TODO use access key
    public Task<UserDto> PrefetchUser(Guid serverId, Guid userId)
        => this.GetGrainFactory().GetGrain<IUserGrain>(userId).GetMe().ToDto();

    public async Task<UserProfileDto> PrefetchProfile(Guid serverId, Guid userId)
        => await this.GetGrainFactory().GetGrain<IServerGrain>(serverId).PrefetchProfile(userId);

    public async Task<List<ArchetypeDto>> GetServerArchetypes(Guid serverId)
        => await this.GetGrainFactory().GetGrain<IEntitlementGrain>(serverId).GetServerArchetypes();

    public async Task<List<ArchetypeDtoGroup>> GetDetailedServerArchetypes(Guid serverId)
        => await this.GetGrainFactory().GetGrain<IEntitlementGrain>(serverId).GetFullyServerArchetypes();

    public Task<ArchetypeDto> CreateArchetypeAsync(Guid serverId, string name)
        => this.GetGrainFactory().GetGrain<IEntitlementGrain>(serverId).CreateArchetypeAsync(name);

    public Task<ArchetypeDto?> UpdateArchetypeAsync(Guid serverId, ArchetypeDto dto)
    {
        dto.ServerId = serverId;
        return this.GetGrainFactory().GetGrain<IEntitlementGrain>(serverId).UpdateArchetypeAsync(dto);
    }

    public Task<ChannelEntitlementOverwrite?>
        UpsertArchetypeEntitlementForChannel(Guid serverId, Guid channelId, Guid archetypeId,
            ArgonEntitlement deny, ArgonEntitlement allow)
        => this
           .GetGrainFactory()
           .GetGrain<IEntitlementGrain>(serverId)
           .UpsertArchetypeEntitlementForChannel(channelId, archetypeId, deny, allow);

    public Task<bool> SetArchetypeToMember(Guid serverId, Guid memberId, Guid archetypeId, bool isGrant)
        => this
           .GetGrainFactory()
           .GetGrain<IEntitlementGrain>(serverId)
           .SetArchetypeToMember(memberId, archetypeId, isGrant);
}