namespace Argon;

using ArchetypeModel;
using Argon.Shared.Servers;
using Streaming.Events;
using Users;

[TsInterface]
public interface IServerInteraction : IArgonService
{
    // manage channels
    Task                        CreateChannel(CreateChannelRequest request);
    Task                        DeleteChannel(Guid serverId, Guid channelId);
    Task<List<RealtimeChannel>> GetChannels(Guid serverId);

    Task<List<ArgonMessageDto>> GetMessages(Guid channelId, int count, int offset);
    Task<List<ArgonMessageDto>> QueryMessages(Guid channelId, ulong? @from, int limit);
    Task                        SendMessage(Guid channelId, string text, List<MessageEntity> entities, ulong? replyTo);

    Task<List<RealtimeServerMember>> GetMembers(Guid serverId);
    Task<RealtimeServerMember>       GetMember(Guid serverId, Guid userId);

    Task<Either<string, JoinToChannelError>> JoinToVoiceChannel(Guid serverId, Guid channelId);

    Task DisconnectFromVoiceChannel(Guid serverId, Guid channelId);


    Task<List<InviteCodeEntity>> GetInviteCodes(Guid serverId);
    Task<InviteCode>             CreateInviteCode(Guid serverId, TimeSpan expiration);

    Task<UserDto> PrefetchUser(Guid serverId, Guid userId);

    Task<UserProfileDto> PrefetchProfile(Guid serverId, Guid userId);

    Task<List<ArchetypeDto>> GetServerArchetypes(Guid serverId);

    Task<ArchetypeDto> CreateArchetypeAsync(Guid serverId, string name);

    Task<ArchetypeDto?> UpdateArchetypeAsync(Guid serverId, ArchetypeDto dto);

    Task<bool> SetArchetypeToMember(Guid serverId, Guid memberId, Guid archetypeId, bool isGrant);

    Task<List<ArchetypeDtoGroup>> GetDetailedServerArchetypes(Guid serverId);

    Task<ChannelEntitlementOverwrite?>
        UpsertArchetypeEntitlementForChannel(Guid serverId, Guid channelId, Guid archetypeId,
            ArgonEntitlement deny, ArgonEntitlement allow);
}