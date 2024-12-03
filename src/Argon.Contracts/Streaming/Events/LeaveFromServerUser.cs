namespace Argon.Streaming.Events;

[TsInterface, MessagePackObject(true)]
public record LeaveFromServerUser(Guid userId) : ArgonEvent<LeaveFromServerUser>;