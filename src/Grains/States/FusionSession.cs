namespace Grains.States;

using MemoryPack;

[GenerateSerializer, Serializable, MemoryPackable, Alias(nameof(FusionSession))]
public sealed partial class FusionSession
{
    [Id(0)]
    public required Guid Id { get; set; } = Guid.Empty;
    [Id(1)]
    public required bool IsAuthorized { get; set; }
}