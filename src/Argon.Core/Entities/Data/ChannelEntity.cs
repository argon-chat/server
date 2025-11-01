namespace Argon.Entities;

using Microsoft.EntityFrameworkCore.Metadata.Builders;

public record ChannelEntity :
    OrderableArgonEntity,
    IArchetypeObject,
    IEntityTypeConfiguration<ChannelEntity>,
    IMapper<ChannelEntity, ArgonChannel>
{
    public         ChannelType ChannelType { get; set; }
    public         Guid        SpaceId     { get; set; }
    public virtual SpaceEntity Space       { get; set; }


    [MaxLength(128)]
    public string Name { get; set; } = string.Empty;
    [MaxLength(1024)]
    public string? Description { get; set; } = null;

    public TimeSpan? SlowMode              { get; set; }
    public bool      DoNotRestrictBoosters { get; set; }

    public virtual ICollection<ChannelEntitlementOverwriteEntity> EntitlementOverwrites { get; set; }
        = new List<ChannelEntitlementOverwriteEntity>();
    public ICollection<IArchetypeOverwrite> Overwrites
        => EntitlementOverwrites.OfType<IArchetypeOverwrite>().ToList();

    public static ArgonChannel Map(scoped in ChannelEntity self)
        => new(self.ChannelType, self.SpaceId, self.Id, self.Name, self.Description, Guid.Empty);

    public void Configure(EntityTypeBuilder<ChannelEntity> builder)
    {
        builder.HasOne(c => c.Space)
           .WithMany(s => s.Channels)
           .HasForeignKey(c => c.SpaceId);

        builder.HasIndex(x => new
        {
            x.SpaceId
        });
    }
}