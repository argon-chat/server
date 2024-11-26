namespace Argon;

using Argon.Contracts;
using MemoryPack;
using Orleans;
using Reinforced.Typings.Attributes;

[Serializable, GenerateSerializer, Alias(nameof(JwtToken)), TsInterface]
public record JwtToken([field: Id(0)] string token);