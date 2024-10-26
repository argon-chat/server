namespace Argon.Api.Grains.Persistence.States;

[GenerateSerializer]
[Serializable]
[Alias(nameof(UserToServerRelations))]
public sealed record UserToServerRelations
{
    [Id(0)] public List<UserToServerRelation> Servers { get; private set; } = [];
}