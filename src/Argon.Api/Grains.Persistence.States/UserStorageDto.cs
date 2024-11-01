namespace Argon.Api.Grains.Persistence.States;

using Contracts;
using Mapster;
using MemoryPack;

[GenerateSerializer]
[Serializable]
[MemoryPackable]
[Alias(nameof(UserStorageDto))]
[GenerateMapper]
public sealed partial record UserStorageDto
{
    [Id(0)] public Guid Id { get; set; }
    [Id(1)] public string? Email { get; set; } = string.Empty;
    [Id(2)] public string Username { get; set; } = string.Empty;
    [Id(3)] public string? PhoneNumber { get; set; } = string.Empty;
    [Id(4)] public string? AvatarUrl { get; set; } = string.Empty;
    [Id(5)] public DateTime CreatedAt { get; set; }
    [Id(6)] public DateTime UpdatedAt { get; set; }

    public static implicit operator UserResponse(UserStorageDto userStorageDto)
    {
        return new UserResponse(
            userStorageDto.Id,
            userStorageDto.Username,
            userStorageDto.AvatarUrl,
            userStorageDto.CreatedAt,
            userStorageDto.UpdatedAt
        );
    }
}