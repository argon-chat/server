namespace Argon.Api.Grains.Persistence.States;

using MemoryPack;

[GenerateSerializer, Serializable, MemoryPackable, Alias(alias: nameof(FusionSession))]
public partial class FusionSession
{
    [Id(id: 0)] public required Guid Id           { get; set; } = Guid.Empty;
    [Id(id: 1)] public required bool IsAuthorized { get; set; }
}