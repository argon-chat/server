namespace Argon.Streaming.Events;

[TsInterface, MessagePackObject(true)]
public record ChannelCreated(Channel channel) : ArgonEvent<ChannelCreated>;