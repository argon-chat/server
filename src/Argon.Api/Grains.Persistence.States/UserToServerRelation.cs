namespace Argon.Api.Grains.Persistence.States;

using MemoryPack;

[GenerateSerializer]
[Serializable]
[MemoryPackable]
[Alias(nameof(UserToServerRelation))]
public sealed record UserToServerRelation
{
    [Id(1)] public Guid ServerId { get; set; }
    [Id(2)] public DateTime Joined { get; } = DateTime.UtcNow;
    [Id(3)] public ServerRole Role { get; set; } = ServerRole.User;
    [Id(4)] public string Username { get; set; } = string.Empty;
    [Id(5)] public string CustomUsername { get; set; } = string.Empty;
}