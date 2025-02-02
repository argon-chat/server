namespace Argon.Streaming.Events;

[TsInterface, MessagePackObject(true)]
public record MessageSent(ArgonMessage message) : ArgonEvent<MessageSent>;