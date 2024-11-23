namespace Argon;

using Argon.Contracts;
using MemoryPack;
using Orleans;
using Reinforced.Typings.Attributes;

[Serializable, GenerateSerializer, MemoryPackable, Alias(nameof(JwtToken)), TsInterface]
public partial record struct JwtToken([field: Id(0)] string token);