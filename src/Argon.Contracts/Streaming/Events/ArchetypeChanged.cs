namespace Argon.Streaming.Events;

using ArchetypeModel;

[TsInterface, MessagePackObject(true)]
public record ArchetypeChanged(ArchetypeDto dto) : ArgonEvent<ArchetypeChanged>;