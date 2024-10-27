namespace Argon.Api.Grains.Persistence.States;

using MemoryPack;

[GenerateSerializer]
[Serializable]
[MemoryPackable]
[Alias(nameof(ServerStorage))]
public sealed partial record ServerStorage
{
    [Id(0)] public Guid Id { get; set; } = Guid.Empty;
    [Id(1)] public string Name { get; set; } = string.Empty;
    [Id(2)] public string Description { get; set; } = string.Empty;
    [Id(5)] public string AvatarUrl { get; set; } = string.Empty;
    [Id(6)] public List<Guid> Channels { get; set; } = [];
    [Id(3)] public DateTime CreatedAt { get; } = DateTime.UtcNow;
    [Id(4)] public DateTime UpdatedAt { get; set; } = DateTime.UtcNow;
}