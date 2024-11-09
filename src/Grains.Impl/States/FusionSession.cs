namespace Grains.Impl.States;

using MemoryPack;
using Orleans;

[GenerateSerializer, Serializable, MemoryPackable, Alias(nameof(FusionSession))]
public class FusionSession
{
    [Id(0)]
    public required Guid Id { get; set; } = Guid.Empty;
    [Id(1)]
    public required bool IsAuthorized { get; set; }
}