namespace Argon;

using ArchetypeModel;
using Microsoft.EntityFrameworkCore;

[MessagePackObject(true), Index("CreatorId"), TsInterface]
public abstract record ArgonEntityWithOwnership : ArgonEntity
{
    [TsIgnore]
    public Guid CreatorId { get; set; }
}

[MessagePackObject(true), TsInterface]
public abstract record OrderableArgonEntity : ArgonEntityWithOwnership, IFractionalOrder
{
    [MaxLength(64)]
    public string FractionalIndex { get; set; }
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