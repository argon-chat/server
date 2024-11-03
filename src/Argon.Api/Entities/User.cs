namespace Argon.Api.Entities;

using System.ComponentModel.DataAnnotations;

public sealed class User : ApplicationRecord
{
    [Required] [MaxLength(255)] [Id(3)] public string Email { get; set; } = string.Empty;

    [MaxLength(255)]
    [MinLength(6)]
    [Id(4)]
    public string? Username { get; set; } = string.Empty;

    [MaxLength(30)] [Id(5)] public string? PhoneNumber { get; set; } = string.Empty;
    [MaxLength(511)] [Id(6)] public string? PasswordDigest { get; set; } = string.Empty;
    [MaxLength(1023)] [Id(7)] public string? AvatarUrl { get; set; } = string.Empty;
    [MaxLength(7)] [Id(8)] public string? OTP { get; set; } = string.Empty;
    [Id(9)] public DateTime? DeletedAt { get; set; } = null;
}