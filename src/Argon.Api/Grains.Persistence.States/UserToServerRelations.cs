namespace Argon.Api.Grains.Persistence.States;

using MemoryPack;

[GenerateSerializer]
[Serializable]
[MemoryPackable]
[Alias(nameof(UserToServerRelations))]
public sealed partial record UserToServerRelations
{
    [Id(0)] public List<UserToServerRelation> Servers { get; private set; } = [];
}