namespace Argon.Api.Grains.Persistence.States;

using System.Runtime.Serialization;
using MemoryPack;
using MessagePack;

[GenerateSerializer]
[Serializable]
[MemoryPackable]
[Alias(nameof(UserStorage))]
public sealed partial record UserStorage
{
    [property: DataMember(Order = 0)]
    [property: MemoryPackOrder(0)]
    [property: Key(0)]
    [Id(0)]
    public Guid Id { get; set; } = Guid.Empty;

    [property: DataMember(Order = 1)]
    [property: MemoryPackOrder(1)]
    [property: Key(1)]
    [Id(1)]
    public string Username { get; set; } = string.Empty;

    [property: DataMember(Order = 2)]
    [property: MemoryPackOrder(2)]
    [property: Key(2)]
    [Id(2)]
    public string Password { get; set; } = string.Empty;

    [property: DataMember(Order = 5)]
    [property: MemoryPackOrder(5)]
    [property: Key(5)]
    [Id(5)]
    public string AvatarUrl { get; set; } = string.Empty;

    [property: DataMember(Order = 3)]
    [property: MemoryPackOrder(3)]
    [property: Key(3)]
    [Id(3)]
    public DateTime CreatedAt { get; } = DateTime.UtcNow;

    [property: DataMember(Order = 4)]
    [property: MemoryPackOrder(4)]
    [property: Key(4)]
    [Id(4)]
    public DateTime UpdatedAt { get; set; } = DateTime.UtcNow;

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