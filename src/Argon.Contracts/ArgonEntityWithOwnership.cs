namespace Argon;

using Microsoft.EntityFrameworkCore;

[MessagePackObject(true), Index("CreatorId"), TsInterface]
public abstract record ArgonEntityWithOwnership : ArgonEntity
{
    [TsIgnore]
    public Guid CreatorId { get; set; }
}