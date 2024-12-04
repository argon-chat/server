namespace Argon;

using Streaming.Events;

[TsInterface]
public interface IServerInteraction : IArgonService
{
    Task         CreateChannel(CreateChannelRequest request);
    Task         DeleteChannel(Guid serverId, Guid channelId);
    Task<string> JoinToVoiceChannel(Guid serverId, Guid channelId);


    Task<List<RealtimeChannel>>      GetChannels(Guid serverId);
    Task<List<RealtimeServerMember>> GetMembers(Guid serverId);
}