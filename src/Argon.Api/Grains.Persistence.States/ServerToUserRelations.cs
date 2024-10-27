namespace Argon.Api.Grains.Persistence.States;

using MemoryPack;

[GenerateSerializer]
[Serializable]
[MemoryPackable]
[Alias(nameof(ServerToUserRelations))]
public sealed record ServerToUserRelations
{
    [Id(0)] public List<UserToServerRelation> Users { get; private set; } = [];
}