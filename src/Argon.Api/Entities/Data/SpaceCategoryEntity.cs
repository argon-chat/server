namespace Argon.Entities;

using Microsoft.EntityFrameworkCore.Metadata.Builders;

public record SpaceCategoryEntity : OrderableArgonEntity, IArchetypeObject, IEntityTypeConfiguration<SpaceCategoryEntity>
{
    public Guid SpaceId { get; set; }
    public virtual SpaceEntity Space { get; set; }
    public virtual ICollection<ChannelEntity> Channels { get; set; }

    [MaxLength(64)]
    public string Title { get; set; }

    public virtual ICollection<ChannelEntitlementOverwriteEntity> EntitlementOverwrites { get; set; }
        = new List<ChannelEntitlementOverwriteEntity>();
    public ICollection<IArchetypeOverwrite> Overwrites 
        => EntitlementOverwrites.OfType<IArchetypeOverwrite>().ToList();

    public void Configure(EntityTypeBuilder<SpaceCategoryEntity> builder)
    {
        builder.HasOne(x => x.Space)
           .WithMany(x => x.SpaceCategories)
           .HasForeignKey(x => x.SpaceId)
           .OnDelete(DeleteBehavior.Cascade);

        builder.HasMany(x => x.Channels)
           .WithOne(x => x.Category)
           .HasForeignKey(x => x.CategoryId)
           .OnDelete(DeleteBehavior.Cascade);
    }
}
