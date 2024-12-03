namespace Argon.Streaming.Events;

[TsInterface, MessagePackObject(true)]
public record ChannelRemoved(Guid channelId) : ArgonEvent<ChannelRemoved>;