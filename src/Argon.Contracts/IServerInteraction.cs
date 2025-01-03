namespace Argon;

using Argon.Shared.Servers;
using Streaming.Events;

[TsInterface]
public interface IServerInteraction : IArgonService
{
    // manage channels
    Task         CreateChannel(CreateChannelRequest request);
    Task         DeleteChannel(Guid serverId, Guid channelId);
    Task<List<RealtimeChannel>> GetChannels(Guid serverId);


    Task<List<RealtimeServerMember>> GetMembers(Guid serverId);

    Task<string> JoinToVoiceChannel(Guid serverId, Guid channelId);


    Task<List<InviteCodeEntity>> GetInviteCodes(Guid serverId);
}