namespace Argon;

[MessagePackObject(true), TsInterface]
public abstract record ArgonEntity
{
    [System.ComponentModel.DataAnnotations.Key]
    public Guid Id { get; set; }

    [IgnoreMember, TsIgnore]
    public DateTimeOffset CreatedAt { get; set; }
    [IgnoreMember, TsIgnore]
    public DateTimeOffset UpdatedAt { get; set; }
    [IgnoreMember, TsIgnore]
    public DateTimeOffset? DeletedAt { get; set; }

    [IgnoreMember, TsIgnore]
    public bool IsDeleted { get; set; }
}

[MessagePackObject(true), TsInterface]
public abstract record ArgonEntity<T>
{
    [System.ComponentModel.DataAnnotations.Key]
    public T Id { get; set; }

    public DateTimeOffset CreatedAt { get; set; }
    public DateTimeOffset UpdatedAt { get; set; }
    public DateTimeOffset? DeletedAt { get; set; }
    public bool IsDeleted { get; set; }
}

[MessagePackObject(true), TsInterface]
public abstract record ArgonEntityNoKey
{
    public DateTimeOffset CreatedAt { get; set; }
    public DateTimeOffset UpdatedAt { get; set; }
    public DateTimeOffset? DeletedAt { get; set; }
    public bool IsDeleted { get; set; }
}