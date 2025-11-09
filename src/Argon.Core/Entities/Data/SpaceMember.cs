namespace Argon.Entities;

using ion.runtime;
using Microsoft.EntityFrameworkCore.Metadata.Builders;

public record SpaceMemberEntity : ArgonEntityWithOwnership, IEntityTypeConfiguration<SpaceMemberEntity>, IMapper<SpaceMemberEntity, SpaceMember>
{
    public Guid SpaceId { get; set; }
    public Guid UserId  { get; set; }

    public virtual UserEntity  User  { get; set; }
    public virtual SpaceEntity Space { get; set; }

    public DateTimeOffset JoinedAt { get; set; }

    public ICollection<SpaceMemberArchetypeEntity> SpaceMemberArchetypes { get; set; }
        = new List<SpaceMemberArchetypeEntity>();

    public static SpaceMember Map(scoped in SpaceMemberEntity self)
        => new(
            self.UserId,
            self.SpaceId,
            self.JoinedAt.UtcDateTime,
            self.Id,
            UserEntity.Map(self.User),
            new IonArray<SpaceMemberArchetype>(IMapper<SpaceMemberArchetypeEntity, SpaceMemberArchetype>.MapCollection(self.SpaceMemberArchetypes)));


    public void Configure(EntityTypeBuilder<SpaceMemberEntity> builder)
    {
        builder.HasOne(x => x.Space)
           .WithMany(x => x.Users)
           .HasForeignKey(x => x.SpaceId);

        builder.HasOne(x => x.User)
           .WithMany(x => x.ServerMembers)
           .HasForeignKey(x => x.UserId);

        builder.HasIndex(x => x.UserId)
           .IncludeProperties(x => new
            {
                x.SpaceId,
                x.IsDeleted
            })
           .IsCreatedConcurrently();
    }
}