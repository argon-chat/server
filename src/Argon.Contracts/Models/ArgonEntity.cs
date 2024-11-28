namespace Argon.Contracts.Models;

using MessagePack;
using Microsoft.EntityFrameworkCore;
using Reinforced.Typings.Attributes;

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


[MessagePackObject(true), Index("CreatorId"), TsInterface]
public abstract record ArgonEntityWithOwnership : ArgonEntity
{
    [TsIgnore]
    public Guid CreatorId { get; set; }
}