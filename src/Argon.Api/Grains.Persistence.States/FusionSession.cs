namespace Argon.Api.Grains.Persistence.States;

[GenerateSerializer, Serializable, MemoryPackable, Alias(nameof(FusionSession))]
public partial class FusionSession
{
    [Id(0)]
    public required Guid Id { get; set; } = Guid.Empty;
    [Id(1)]
    public required bool IsAuthorized { get; set; }
}