namespace Argon;

using ArchetypeModel;
using Microsoft.EntityFrameworkCore;

[Index("CreatorId")]
public abstract record ArgonEntityWithOwnership : ArgonEntity
{
    public Guid CreatorId { get; set; }
}

public abstract record OrderableArgonEntity : ArgonEntityWithOwnership, IFractionalOrder
{
    [MaxLength(64)]
    public string FractionalIndex { get; set; }
}

[Index("CreatorId")]
public abstract record ArgonEntityWithOwnership<T> : ArgonEntity<T>
{
    public Guid CreatorId { get; set; }
}

[Index("CreatorId")]
public abstract record ArgonEntityWithOwnershipNoKey : ArgonEntityNoKey
{
    public Guid CreatorId { get; set; }
}