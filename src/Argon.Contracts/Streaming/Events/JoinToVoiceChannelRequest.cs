namespace Argon.Streaming.Events;

[TsInterface, MessagePackObject(true)]
public record JoinToVoiceChannelRequest(Guid serverId, Guid channelId);