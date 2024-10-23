namespace Argon.Api.Grains.Persistence.States;

[GenerateSerializer]
[Serializable]
[Alias(nameof(UserToServerRelations))]
public sealed record UserToServerRelations
{
    [Id(0)] public List<UserToServerRelation> Servers { get; private set; } = [];
}

[GenerateSerializer]
[Serializable]
[Alias(nameof(ServerToUserRelations))]
public sealed record ServerToUserRelations
{
    [Id(0)] public List<UserToServerRelation> Users { get; private set; } = [];
}

[GenerateSerializer]
[Serializable]
[Alias(nameof(UserToServerRelation))]
public sealed record UserToServerRelation
{
    [Id(1)] public Guid ServerId { get; set; }
    [Id(2)] public DateTime Joined { get; } = DateTime.UtcNow;
    [Id(3)] public ServerRole Role { get; set; } = ServerRole.User;
    [Id(4)] public string Username { get; set; } = string.Empty;
    [Id(5)] public string CustomUsername { get; set; } = string.Empty;
}

[GenerateSerializer]
[Serializable]
[Alias(nameof(ServerRole))]
public enum ServerRole : ushort // TODO: sort out roles and how we actually want to handle them
{
    User,
    Admin,
    Owner
}