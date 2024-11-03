namespace Argon.Api.Entities;

using System.ComponentModel.DataAnnotations;
using Grains.Persistence.States;

public sealed class User : ApplicationRecord
{
    [Required]
    [MaxLength(255)]
    [MinLength(12)]
    public string Email { get; set; } = string.Empty;

    [MaxLength(255)] [MinLength(6)] public string? Username { get; set; } = string.Empty;

    [MaxLength(30)] public string? PhoneNumber { get; set; } = string.Empty;
    [MaxLength(511)] public string? PasswordDigest { get; set; } = string.Empty;
    [MaxLength(1023)] public string? AvatarUrl { get; set; } = string.Empty;
    [MaxLength(7)] public string? OTP { get; set; } = string.Empty;


    public static implicit operator UserStorageDto(User user)
    {
        return new UserStorageDto
        {
            Id = user.Id,
            Username = user.Username,
            Email = user.Email,
            PhoneNumber = user.PhoneNumber,
            AvatarUrl = user.AvatarUrl,
            CreatedAt = user.CreatedAt,
            UpdatedAt = user.UpdatedAt
        };
    }
}