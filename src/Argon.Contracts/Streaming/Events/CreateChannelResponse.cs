namespace Argon.Streaming.Events;

[TsInterface, MessagePackObject(true)]
public record CreateChannelResponse(Guid serverId, Guid channelId);