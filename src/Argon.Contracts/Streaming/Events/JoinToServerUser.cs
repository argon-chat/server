namespace Argon.Streaming.Events;

[TsInterface, MessagePackObject(true)]
public record JoinToServerUser(Guid userId) : ArgonEvent<JoinToServerUser>;