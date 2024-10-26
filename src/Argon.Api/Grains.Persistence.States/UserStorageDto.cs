namespace Argon.Api.Grains.Persistence.States;

[GenerateSerializer]
[Serializable]
[Alias(nameof(UserStorageDto))]
public sealed record UserStorageDto
{
    [Id(0)] public Guid Id { get; set; }
    [Id(1)] public string Username { get; set; } = string.Empty;
    [Id(4)] public string AvatarUrl { get; set; } = string.Empty;
    [Id(2)] public DateTime CreatedAt { get; set; }
    [Id(3)] public DateTime UpdatedAt { get; set; }
}