namespace Argon.Entities;

using Argon.Features.EF;
using Microsoft.EntityFrameworkCore.Metadata.Builders;
using System.Drawing;

public record ArchetypeEntity : ArgonEntityWithOwnership, IArchetype, IEntityTypeConfiguration<ArchetypeEntity>, IMapper<ArchetypeEntity, Archetype>
{
    public static readonly Guid DefaultArchetype_Everyone
        = Guid.Parse("11111111-3333-0000-1111-111111111111");
    public static readonly Guid DefaultArchetype_Owner
        = Guid.Parse("11111111-4444-0000-1111-111111111111");

    
    public virtual SpaceEntity Space { get; set; }
    
    public Guid SpaceId { get; set; }

    [MaxLength(64)]
    public string Name { get; set; } = string.Empty;
    [MaxLength(512)]
    public string Description { get; set; } = string.Empty;

    public ArgonEntitlement Entitlement { get; set; }

    public bool IsMentionable { get; set; }
    public bool IsLocked      { get; set; }
    public bool IsHidden      { get; set; }
    public bool IsGroup       { get; set; }
    public bool IsDefault     { get; set; }

    public Color Colour { get; set; }
    [MaxLength(128)]
    public string? IconFileId { get; set; } = null;

    public virtual ICollection<SpaceMemberArchetypeEntity> SpaceMemberRoles { get; set; }
        = new List<SpaceMemberArchetypeEntity>();

    public void Configure(EntityTypeBuilder<ArchetypeEntity> builder)
    {
        builder.HasOne(x => x.Space)
           .WithMany(x => x.Archetypes)
           .HasForeignKey(x => x.SpaceId);

        builder.Property(x => x.Colour)
           .HasConversion<ColourConverter>();
    }

    public static Archetype Map(scoped in ArchetypeEntity self)
        => new(self.Id, self.SpaceId, self.Name, self.Description, self.IsMentionable, self.Colour.ToArgb(), self.IsHidden, self.IsLocked,
            self.IsGroup, self.IsDefault, self.IconFileId, self.Entitlement);
}