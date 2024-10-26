namespace Argon.Api.Grains.Persistence.States;

[GenerateSerializer]
[Serializable]
[Alias(nameof(UserStorage))]
public sealed record UserStorage
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