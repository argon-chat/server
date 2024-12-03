namespace Argon;

[MessagePackObject(true), TsInterface]
public abstract record ArgonEntity
{
    [System.ComponentModel.DataAnnotations.Key]
    public Guid Id { get; set; }

    [IgnoreMember, TsIgnore]
    public DateTime CreatedAt { get; set; }
    [IgnoreMember, TsIgnore]
    public DateTime UpdatedAt { get; set; }
    [IgnoreMember, TsIgnore]
    public DateTime? DeletedAt { get; set; }

    [IgnoreMember, TsIgnore]
    public bool IsDeleted { get; set; }
}