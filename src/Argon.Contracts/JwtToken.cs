namespace Argon;

using MemoryPack;
using Orleans;

[Serializable, GenerateSerializer, MemoryPackable, Alias(nameof(JwtToken))]
public partial record struct JwtToken([field: Id(0)] string token);