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



    public         Guid        SpaceId { get; set; }
    public virtual SpaceEntity Space   { get; set; }


    public string Name        { get; init; } = string.Empty;
    public string Description { get; init; } = string.Empty;

    public ArgonEntitlement Entitlement { get; set; }

    public bool IsMentionable { get; set; }
    public bool IsLocked      { get; init; }
    public bool IsHidden      { get; init; }
    public bool IsGroup       { get; set; }
    public bool IsDefault     { get; set; }

    public Color   Colour     { get; set; }
    public string? IconFileId { get; init; }

    public virtual ICollection<SpaceMemberArchetypeEntity> SpaceMemberRoles { get; set; }
        = new List<SpaceMemberArchetypeEntity>();

    public void Configure(EntityTypeBuilder<ArchetypeEntity> builder)
    {
        builder.HasOne(x => x.Space)
           .WithMany(x => x.Archetypes)
           .HasForeignKey(x => x.SpaceId);

        builder.Property(x => x.Colour)
           .HasConversion<ColourConverter>();

        builder.Property(x => x.Entitlement)
           .HasColumnType("BIGINT")
           .HasConversion(
                v => (long)v,
                v => (ArgonEntitlement)(ulong)v
            );
    }

    public static Archetype Map(scoped in ArchetypeEntity self)
        => new(self.Id, self.SpaceId, self.Name, self.Description, self.IsMentionable, self.Colour.ToArgb(), self.IsHidden, self.IsLocked,
            self.IsGroup, self.IsDefault, self.IconFileId, self.Entitlement);
}