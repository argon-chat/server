namespace Argon.Api.Entities;

using System.ComponentModel.DataAnnotations;

public class User : ApplicationRecord
{
    [MaxLength(255)] [MinLength(12)] public string? Email { get; set; } = string.Empty;

    [Required]
    [MaxLength(255)]
    [MinLength(6)]
    public string Username { get; set; } = string.Empty;

    [MaxLength(30)] public string? PhoneNumber { get; set; } = string.Empty;
    [Required] [MaxLength(511)] public string PasswordDigest { get; set; } = string.Empty;
    [MaxLength(1023)] public string? AvatarUrl { get; set; } = string.Empty;
}