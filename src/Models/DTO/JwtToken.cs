namespace Models.DTO;

using MemoryPack;
using Orleans;

[Serializable, GenerateSerializer, MemoryPackable, Alias(nameof(JwtToken))]
public record struct JwtToken(string token);