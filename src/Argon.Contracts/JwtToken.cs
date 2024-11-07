namespace Argon;

using MemoryPack;
using Orleans;

[Serializable, GenerateSerializer, MemoryPackable, Alias(nameof(JwtToken))]
public partial record struct JwtToken(string token);