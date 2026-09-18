namespace Argon.Entities;

using Microsoft.EntityFrameworkCore.Metadata.Builders;

/// <summary>
/// Which loadout a person wears where. A row with no space is the global default; a row with one
/// overrides it in that space.
/// </summary>
public record CosmeticScopeAssignmentEntity : ArgonEntity, IEntityTypeConfiguration<CosmeticScopeAssignmentEntity>
{
    public required Guid UserId    { get; set; }
    public required Guid LoadoutId { get; set; }

    /// <summary>Null means this is the assignment used wherever no space-specific one exists.</summary>
    public Guid? SpaceId { get; set; }

    public virtual UserEntity            User    { get; set; } = null!;
    public virtual CosmeticLoadoutEntity Loadout { get; set; } = null!;

    public void Configure(EntityTypeBuilder<CosmeticScopeAssignmentEntity> builder)
    {
        builder.HasOne(x => x.User)
           .WithMany()
           .HasForeignKey(x => x.UserId)
           .OnDelete(DeleteBehavior.Cascade);

        builder.HasOne(x => x.Loadout)
           .WithMany()
           .HasForeignKey(x => x.LoadoutId)
           .OnDelete(DeleteBehavior.Cascade);

        // One assignment per person per space, and one global. Two indexes rather than one because
        // Postgres treats nulls as distinct in a unique index, so a single (UserId, SpaceId) index
        // would let a person accumulate any number of global rows.
        builder.HasIndex(x => new
            {
                x.UserId,
                x.SpaceId
            })
           .IsUnique()
           .HasFilter("\"SpaceId\" IS NOT NULL AND \"IsDeleted\" = false");

        builder.HasIndex(x => x.UserId)
           .IsUnique()
           .HasFilter("\"SpaceId\" IS NULL AND \"IsDeleted\" = false");
    }
}
