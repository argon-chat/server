namespace Argon.Api.Grains.Persistence.States;

using MemoryPack;

[GenerateSerializer]
[Serializable]
[MemoryPackable]
[Alias(nameof(UserStorage))]
public sealed partial record UserStorage
{
    [Id(0)] public Guid Id { get; set; } = Guid.Empty;
    [Id(1)] public string Username { get; set; } = string.Empty;
    [Id(2)] public string Password { get; set; } = string.Empty;
    [Id(5)] public string AvatarUrl { get; set; } = string.Empty;
    [Id(3)] public DateTime CreatedAt { get; } = DateTime.UtcNow;
    [Id(4)] public DateTime UpdatedAt { get; set; } = DateTime.UtcNow;

    public static implicit operator UserStorageDto(UserStorage userStorage)
    {
        return new UserStorageDto
        {
            Id = userStorage.Id,
            Username = userStorage.Username,
            AvatarUrl = userStorage.AvatarUrl,
            CreatedAt = userStorage.CreatedAt,
            UpdatedAt = userStorage.UpdatedAt
        };
    }
}