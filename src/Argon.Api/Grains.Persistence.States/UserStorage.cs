namespace Argon.Api.Grains.Persistence.States;

[GenerateSerializer]
[Alias(nameof(UserStorage))]
public sealed record class UserStorage
{
    [Id(0)] public Guid Id { get; set; } = Guid.NewGuid();
    [Id(1)] public string Username { get; set; } = string.Empty;
    [Id(2)] public string Password { get; set; } = string.Empty;
    [Id(3)] public DateTime CreatedAt { get; private set; } = DateTime.UtcNow;
    [Id(4)] public DateTime UpdatedAt { get; set; } = DateTime.UtcNow;

    public static implicit operator UserStorageDto(UserStorage userStorage) =>
        new UserStorageDto
        {
            Id = userStorage.Id,
            Username = userStorage.Username,
            CreatedAt = userStorage.CreatedAt,
            UpdatedAt = userStorage.UpdatedAt
        };
}

[GenerateSerializer]
[Alias(nameof(UserStorageDto))]
public sealed record UserStorageDto
{
    [Id(0)] public Guid Id { get; set; }
    [Id(1)] public string Username { get; set; } = string.Empty;
    [Id(2)] public DateTime CreatedAt { get; set; }
    [Id(3)] public DateTime UpdatedAt { get; set; }
}