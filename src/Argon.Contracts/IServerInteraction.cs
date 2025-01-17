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


    Task<List<RealtimeServerMember>> GetMembers(Guid serverId);

    Task<Either<string, JoinToChannelError>> JoinToVoiceChannel(Guid serverId, Guid channelId);

    Task DisconnectFromVoiceChannel(Guid serverId, Guid channelId);


    Task<List<InviteCodeEntity>> GetInviteCodes(Guid serverId);
    Task<InviteCode>             CreateInviteCode(Guid serverId, TimeSpan expiration);

    Task<User> PrefetchUser(Guid serverId, Guid userId);
}