namespace Argon.Api.Entities;

using System.ComponentModel.DataAnnotations;

public sealed record User
{
    public Guid     Id        { get; init; } = Guid.NewGuid();
    public DateTime CreatedAt { get; init; } = DateTime.UtcNow;
    public DateTime UpdatedAt { get; set; }  = DateTime.UtcNow;
    [Required, MaxLength(255)]
    public string Email { get; set; } = string.Empty;
    [MaxLength(255)]
    public string? Username { get; set; } = string.Empty;
    [MaxLength(30)]
    public string? DisplayName { get; set; } = string.Empty;
    [MaxLength(30)]
    public string? PhoneNumber { get; set; } = string.Empty;
    [MaxLength(511)]
    public string? PasswordDigest { get; set; } = string.Empty;
    [MaxLength(1023)]
    public string? AvatarFileId { get; set; } = string.Empty;
    [MaxLength(128)]
    public string? OtpHash { get;                                    set; } = string.Empty;
    public DateTime?                   DeletedAt              { get; set; }
    public List<UsersToServerRelation> UsersToServerRelations { get; set; } = new();
}


public record UserAgreements
{
    [Key]
    public Guid     Id                        { get; set; } = Guid.NewGuid();
    public bool     AllowedSendOptionalEmails { get; set; }
    public bool     AgreeTOS                  { get; set; }
    public DateTime CreatedAt                 { get; init; } = DateTime.UtcNow;
    public DateTime UpdatedAt                 { get; set; }  = DateTime.UtcNow;

    public virtual User User   { get; set; }
    public         Guid UserId { get; set; }
}

[DataContract, MemoryPackable(GenerateType.CircularReference), Alias(nameof(UserDto))]
public partial record UserDto
{
    [MemoryPackConstructor]
    public UserDto()  { }

    public UserDto(Guid Id,
        DateTime CreatedAt,
        DateTime UpdatedAt,
        string Email,
        string? Username,
        string? PhoneNumber,
        string? AvatarUrl,
        DateTime? DeletedAt,
        List<ServerDto> Servers)
    {
        this.Id          = Id;
        this.CreatedAt   = CreatedAt;
        this.UpdatedAt   = UpdatedAt;
        this.Email       = Email;
        this.Username    = Username;
        this.PhoneNumber = PhoneNumber;
        this.AvatarUrl   = AvatarUrl;
        this.DeletedAt   = DeletedAt;
        this.Servers     = Servers;
    }

    [DataMember(Order = 0), MemoryPackOrder(0), Id(0)]
    public Guid Id { get;            set; }
    [DataMember(Order = 1), MemoryPackOrder(1), Id(1)]
    public DateTime CreatedAt { get; set; }
    [DataMember(Order = 2), MemoryPackOrder(2), Id(2)]
    public DateTime UpdatedAt { get; set; }
    [DataMember(Order = 3), MemoryPackOrder(3), Id(3)]
    public string Email { get; set; }
    [DataMember(Order = 4), MemoryPackOrder(4), Id(4)]
    public string? Username { get; set; }
    [DataMember(Order = 5), MemoryPackOrder(5), Id(5)]
    public string? PhoneNumber { get; set; }
    [DataMember(Order = 6), MemoryPackOrder(6), Id(6)]
    public string? AvatarUrl { get; set; }
    [DataMember(Order = 7), MemoryPackOrder(7), Id(7)]
    public DateTime? DeletedAt { get; set; }
    [DataMember(Order = 8), MemoryPackIgnore, Id(8)]
    public List<ServerDto> Servers { get; set; }
}