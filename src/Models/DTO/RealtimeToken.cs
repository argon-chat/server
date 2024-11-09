namespace Models.DTO;

using MemoryPack;
using Orleans;

[Serializable, GenerateSerializer, MemoryPackable, Alias(nameof(RealtimeToken))]
public record struct RealtimeToken(string value);