namespace Argon.Streaming.Events;

[TsInterface, MessagePackObject(true)]
public record ServerModified(List<string> bag) : ArgonEvent<ServerModified>;