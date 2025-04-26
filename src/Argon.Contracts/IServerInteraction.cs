namespace Argon;

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
    Task                        SendMessage(Guid channelId, string text, List<MessageEntity> entities);

    Task<List<RealtimeServerMember>> GetMember(Guid serverId);
    Task<RealtimeServerMember>       GetMembers(Guid serverId, Guid userId);

    Task<Either<string, JoinToChannelError>> JoinToVoiceChannel(Guid serverId, Guid channelId);

    Task DisconnectFromVoiceChannel(Guid serverId, Guid channelId);


    Task<List<InviteCodeEntity>> GetInviteCodes(Guid serverId);
    Task<InviteCode>             CreateInviteCode(Guid serverId, TimeSpan expiration);

    Task<UserDto> PrefetchUser(Guid serverId, Guid userId);
}