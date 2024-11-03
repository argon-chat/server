namespace Argon.Api.Entities;

using System.ComponentModel.DataAnnotations;
using System.Runtime.Serialization;
using MemoryPack;
using MessagePack;

[DataContract]
[MemoryPackable(GenerateType.VersionTolerant)]
[MessagePackObject]
[Serializable]
[GenerateSerializer]
[Alias("Argon.Api.Entities.User")]
public sealed record User
{
    [System.ComponentModel.DataAnnotations.Key]
    [Id(0)]
    [MemoryPackOrder(0)]
    [DataMember(Order = 0)]
    public Guid Id { get; private set; } = Guid.Empty;

    [Id(1)]
    [MemoryPackOrder(1)]
    [DataMember(Order = 1)]
    public DateTime CreatedAt { get; private set; } = DateTime.UtcNow;

    [Id(2)]
    [MemoryPackOrder(2)]
    [DataMember(Order = 2)]
    public DateTime UpdatedAt { get; set; } = DateTime.UtcNow;

    [Required]
    [MaxLength(255)]
    [Id(3)]
    [MemoryPackOrder(3)]
    [DataMember(Order = 3)]
    public string Email { get; set; } = string.Empty;

    [MaxLength(255)]
    [MinLength(6)]
    [Id(4)]
    [MemoryPackOrder(4)]
    [DataMember(Order = 4)]
    public string? Username { get; set; } = string.Empty;

    [MaxLength(30)]
    [Id(5)]
    [MemoryPackOrder(5)]
    [DataMember(Order = 5)]
    public string? PhoneNumber { get; set; } = string.Empty;

    [MaxLength(511)]
    [Id(6)]
    [MemoryPackOrder(6)]
    [DataMember(Order = 6)]
    public string? PasswordDigest { get; set; } = string.Empty;

    [MaxLength(1023)]
    [Id(7)]
    [MemoryPackOrder(7)]
    [DataMember(Order = 7)]
    public string? AvatarUrl { get; set; } = string.Empty;

    [MaxLength(7)]
    [Id(8)]
    [MemoryPackOrder(8)]
    [DataMember(Order = 8)]
    public string? OTP { get; set; } = string.Empty;

    [Id(9)]
    [MemoryPackOrder(9)]
    [DataMember(Order = 9)]
    public DateTime? DeletedAt { get; set; }
}