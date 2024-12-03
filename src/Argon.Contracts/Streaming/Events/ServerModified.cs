namespace Argon.Streaming.Events;

[TsInterface, MessagePackObject(true)]
public record ServerModified(PropertyBag bag) : ArgonEvent<ServerModified>;