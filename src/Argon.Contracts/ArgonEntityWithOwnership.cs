namespace Argon;

using Microsoft.EntityFrameworkCore;

[MessagePackObject(true), Index("CreatorId"), TsInterface]
public abstract record ArgonEntityWithOwnership : ArgonEntity
{
    [TsIgnore]
    public Guid CreatorId { get; set; }
}

[MessagePackObject(true), Index("CreatorId"), TsInterface]
public abstract record ArgonEntityWithOwnership<T> : ArgonEntity<T>
{
    [TsIgnore]
    public Guid CreatorId { get; set; }
}

[MessagePackObject(true), Index("CreatorId"), TsInterface]
public abstract record ArgonEntityWithOwnershipNoKey : ArgonEntityNoKey
{
    public Guid CreatorId { get; set; }
}