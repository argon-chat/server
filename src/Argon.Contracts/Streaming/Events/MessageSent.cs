namespace Argon.Streaming.Events;

[TsInterface, MessagePackObject(true)]
public record MessageSent(ArgonMessageDto message) : ArgonEvent<MessageSent>;