namespace Argon.Api.Grains.Persistence.States;

[GenerateSerializer]
[Serializable]
[Alias(nameof(ServerToUserRelations))]
public sealed record ServerToUserRelations
{
    [Id(0)] public List<UserToServerRelation> Users { get; private set; } = [];
}