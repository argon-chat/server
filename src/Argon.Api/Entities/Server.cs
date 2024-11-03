namespace Argon.Api.Entities;

using System.ComponentModel.DataAnnotations;

public class Server : ApplicationRecord
{
    [MaxLength(255), Id(3)] public string Name { get; set; } = string.Empty;
    [MaxLength(255), Id(4)] public string Description { get; set; } = string.Empty;
    [MaxLength(255), Id(5)] public string AvatarUrl { get; set; } = string.Empty;
}