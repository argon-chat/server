namespace Argon.Api.Entities;

using System.ComponentModel.DataAnnotations;
using Microsoft.EntityFrameworkCore;

public class User : ApplicationRecord
{
    public string Email { get; set; } = string.Empty;
    [Required]
    public string Username { get; set; } = string.Empty;
    public string PhoneNumber { get; set; } = string.Empty;
    [Required]
    public string PasswordDigest { get; set; } = string.Empty;
    public string AvatarUrl { get; set; } = string.Empty;
};