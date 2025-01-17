namespace Argon.Streaming.Events;

[TsInterface, MessagePackObject(true)]
public record ChannelModified(Guid channelId, List<string> bag) : ArgonEvent<ChannelModified>;