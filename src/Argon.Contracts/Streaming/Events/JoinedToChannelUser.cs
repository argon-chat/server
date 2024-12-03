namespace Argon.Streaming.Events;

[TsInterface, MessagePackObject(true)]
public record JoinedToChannelUser(Guid userId, Guid channelId) : ArgonEvent<JoinedToChannelUser>;