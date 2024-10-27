namespace Argon.Api.Grains.Persistence.States;

using MemoryPack;

[GenerateSerializer]
[Serializable]
[MemoryPackable]
[Alias(nameof(ServerChannelsStore))]
public sealed partial record ServerChannelsStore
{
    [Id(0)] public List<Guid> Channels { get; private set; } = [];
}