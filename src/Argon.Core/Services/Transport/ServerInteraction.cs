//namespace Argon.Services;

//using InviteCode = Entities.InviteCode;
//using InviteCodeEntity = Entities.InviteCodeEntity;

//public class ServerInteraction : IServerInteraction
//{
//    public async Task CreateChannel(CreateChannelRequest request)
//        => await this.GetGrainFactory()
//           .GetGrain<ISpaceGrain>(request.spaceId)
//           .CreateChannel(new ChannelInput(request.name, new ChannelEntitlementOverwrite(), request.desc, request.kind));

//    public Task DeleteChannel(Guid serverId, Guid channelId)
//        => this.GetGrainFactory()
//           .GetGrain<ISpaceGrain>(serverId)
//           .DeleteChannel(channelId);

//    public Task<RealtimeServerMember> GetMember(Guid serverId, Guid userId)
//        => this.GetGrainFactory()
//           .GetGrain<ISpaceGrain>(serverId)
//           .GetMember(userId);

//    public async Task<Either<string, JoinToChannelError>> JoinToVoiceChannel(Guid serverId, Guid channelId)
//        => await this.GetGrainFactory()
//           .GetGrain<IChannelGrain>(channelId)
//           .Join();

//    public async Task DisconnectFromVoiceChannel(Guid serverId, Guid channelId)
//    {
//        var user = this.GetUser();
//        await this.GetGrainFactory()
//           .GetGrain<IChannelGrain>(channelId)
//           .Leave(user.id);
//    }

//    public Task<List<RealtimeChannel>> GetChannels(Guid serverId)
//        => this.GetGrainFactory()
//           .GetGrain<ISpaceGrain>(serverId)
//           .GetChannels();

//    public Task<List<RealtimeServerMember>> GetMembers(Guid serverId)
//        => this.GetGrainFactory()
//           .GetGrain<ISpaceGrain>(serverId)
//           .GetMembers();

//    public Task<List<InviteCodeEntity>> GetInviteCodes(Guid serverId)
//        => this.GetGrainFactory()
//           .GetGrain<IServerInvitesGrain>(serverId)
//           .GetInviteCodes();

//    public Task<InviteCode> CreateInviteCode(Guid serverId, TimeSpan expiration)
//    {
//        var user = this.GetUser();
//        return this.GetGrainFactory()
//           .GetGrain<IServerInvitesGrain>(serverId)
//           .CreateInviteLinkAsync(user.id, expiration);
//    }

//    public Task SendMessage(Guid channelId, string text, List<MessageEntity> entities, ulong? replyTo)
//        => this.GetGrainFactory()
//           .GetGrain<IChannelGrain>(channelId)
//           .SendMessage(text, entities, replyTo);

//    public Task<List<ArgonMessageDto>> GetMessages(Guid channelId, int count, int offset)
//        => this.GetGrainFactory()
//           .GetGrain<IChannelGrain>(channelId)
//           .GetMessages(count, offset)
//           .ToDto();

//    public Task<List<ArgonMessageDto>> QueryMessages(Guid channelId, ulong? @from, int limit)
//        => this.GetGrainFactory()
//           .GetGrain<IChannelGrain>(channelId)
//           .QueryMessages(@from, limit)
//           .ToDto();

//    // TODO use access key
//    public Task<UserDto> PrefetchUser(Guid serverId, Guid userId)
//        => this.GetGrainFactory().GetGrain<IUserGrain>(userId).GetMe().ToDto();

//    public async Task<UserProfileDto> PrefetchProfile(Guid serverId, Guid userId)
//        => await this.GetGrainFactory().GetGrain<ISpaceGrain>(serverId).PrefetchProfile(userId);

//    public async Task<List<Archetype>> GetServerArchetypes(Guid serverId)
//        => await this.GetGrainFactory().GetGrain<IEntitlementGrain>(serverId).GetServerArchetypes();

//    public async Task<List<ArchetypeDtoGroup>> GetDetailedServerArchetypes(Guid serverId)
//        => await this.GetGrainFactory().GetGrain<IEntitlementGrain>(serverId).GetFullyServerArchetypes();

//    public Task<Archetype> CreateArchetypeAsync(Guid serverId, string name)
//        => this.GetGrainFactory().GetGrain<IEntitlementGrain>(serverId).CreateArchetypeAsync(name);

//    public Task<Archetype?> UpdateArchetypeAsync(Guid serverId, Archetype dto)
//    {
//        dto.ServerId = serverId;
//        return this.GetGrainFactory().GetGrain<IEntitlementGrain>(serverId).UpdateArchetypeAsync(dto);
//    }

//    public Task<ChannelEntitlementOverwrite?>
//        UpsertArchetypeEntitlementForChannel(Guid serverId, Guid channelId, Guid archetypeId,
//            ArgonEntitlement deny, ArgonEntitlement allow)
//        => this
//           .GetGrainFactory()
//           .GetGrain<IEntitlementGrain>(serverId)
//           .UpsertArchetypeEntitlementForChannel(channelId, archetypeId, deny, allow);

//    public Task<bool> SetArchetypeToMember(Guid serverId, Guid memberId, Guid archetypeId, bool isGrant)
//        => this
//           .GetGrainFactory()
//           .GetGrain<IEntitlementGrain>(serverId)
//           .SetArchetypeToMember(memberId, archetypeId, isGrant);


//    public Task<bool> KickMemberFromChannel(Guid serverId, Guid channelId, Guid memberId)
//        => this.GetGrainFactory().GetGrain<IChannelGrain>(channelId).KickMemberFromChannel(memberId);
//}