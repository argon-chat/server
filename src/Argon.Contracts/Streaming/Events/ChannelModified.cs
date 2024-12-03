namespace Argon.Streaming.Events;

[TsInterface, MessagePackObject(true)]
public record ChannelModified(Guid channelId, PropertyBag bag) : ArgonEvent<ChannelModified>;