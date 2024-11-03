namespace Argon.Api.Grains.Persistence.States;

using MemoryPack;

[GenerateSerializer]
[Serializable]
[MemoryPackable]
[Alias(nameof(UserToServerRelation))]
public sealed partial record UserToServerRelation
{
    [Id(1)] public Guid ServerId { get; set; } = Guid.Empty;
    [Id(2)] public DateTime Joined { get; } = DateTime.UtcNow;
    [Id(3)] public ServerRole Role { get; set; } = ServerRole.User;
    [Id(4)] public Guid UserId { get; set; } = Guid.Empty;
    [Id(5)] public string CustomUsername { get; set; } = string.Empty;
    [Id(6)] public bool IsBanned { get; set; } = false;
    [Id(7)] public bool IsMuted { get; set; } = false;
    [Id(8)] public DateTime? BannedUntil { get; set; } = null;
    [Id(9)] public DateTime? MutedUntil { get; set; } = null;
    [Id(10)] public string AvatarUrl { get; set; } = string.Empty;
    [Id(11)] public string? CustomAvatarUrl { get; set; } = null;
    [Id(12)] public string? BanReason { get; set; } = null;
    [Id(13)] public string? MuteReason { get; set; } = null;
}