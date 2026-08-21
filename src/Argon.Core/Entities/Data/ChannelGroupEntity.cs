namespace Argon.Entities;

using Microsoft.EntityFrameworkCore.Metadata.Builders;

public record ChannelGroupEntity :
    OrderableArgonEntity,
    IEntityTypeConfiguration<ChannelGroupEntity>, IMapper<ChannelGroupEntity, ChannelGroup>
{
    public Guid SpaceId { get; set; }
    public virtual SpaceEntity Space { get; set; }

    [MaxLength(128)]
    public string Name { get; set; } = string.Empty;

    [MaxLength(512)]
    public string? Description { get; set; } = null;

    public bool IsCollapsed { get; set; } = false;

    public virtual ICollection<ChannelEntity> Channels { get; set; }
        = new List<ChannelEntity>();

    public void Configure(EntityTypeBuilder<ChannelGroupEntity> builder)
    {
        builder.HasOne(g => g.Space)
            .WithMany(s => s.ChannelGroups)
            .HasForeignKey(g => g.SpaceId);

        builder.HasIndex(x => new
        {
            x.SpaceId,
            x.FractionalIndex
        });
    }

    public static ChannelGroup Map(scoped in ChannelGroupEntity self)
        => new(self.SpaceId, self.Id, self.Name, self.Description, self.IsCollapsed, string.IsNullOrEmpty(self.FractionalIndex) ? null : self.FractionalIndex);
}