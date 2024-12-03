namespace Argon.Streaming.Events;

[TsInterface, MessagePackObject(true)]
public record LeavedFromChannelUser(Guid userId, Guid channelId) : ArgonEvent<LeavedFromChannelUser>;