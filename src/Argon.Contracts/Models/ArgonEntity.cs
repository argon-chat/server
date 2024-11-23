namespace Argon.Contracts.Models;

using MessagePack;
using Microsoft.EntityFrameworkCore;

[MessagePackObject(true)]
public abstract record ArgonEntity
{
    [System.ComponentModel.DataAnnotations.Key]
    public Guid Id { get; set; }

    [IgnoreMember]
    public DateTime CreatedAt { get; set; }
    [IgnoreMember]
    public DateTime UpdatedAt { get; set; }
    [IgnoreMember]
    public DateTime? DeletedAt { get; set; }

    [IgnoreMember]
    public bool IsDeleted { get; set; }
}


[MessagePackObject(true), Index("CreatorId")]
public abstract record ArgonEntityWithOwnership : ArgonEntity
{
    public Guid CreatorId { get; set; }
}