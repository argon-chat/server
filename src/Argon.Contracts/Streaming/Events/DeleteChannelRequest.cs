namespace Argon.Streaming.Events;

[TsInterface, MessagePackObject(true)]
public record DeleteChannelRequest(Guid serverId, Guid channelId);