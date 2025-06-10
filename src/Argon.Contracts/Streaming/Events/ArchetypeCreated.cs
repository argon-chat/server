namespace Argon.Streaming.Events;

using ArchetypeModel;

[TsInterface, MessagePackObject(true)]
public record ArchetypeCreated(ArchetypeDto dto) : ArgonEvent<ArchetypeCreated>;