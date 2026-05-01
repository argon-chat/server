namespace Argon.Entities;

using ArgonContracts;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata.Builders;

public record SpaceBoostEntity : ArgonEntity, IEntityTypeConfiguration<SpaceBoostEntity>
{
    public Guid            UserId                 { get; set; }
    public Guid?           SpaceId                { get; set; }
    public Guid?           SubscriptionId         { get; set; }
    public DateTimeOffset? AppliedAt              { get; set; }
    public DateTimeOffset? TransferCooldownUntil  { get; set; }
    public DateTimeOffset? ExpiresAt              { get; set; }
    public BoostSource     Source                 { get; set; }

    [MaxLength(256)]
    public string? XsollaTransactionId { get; set; }

    public virtual UserEntity                   User         { get; set; } = null!;
    public virtual SpaceEntity?                 Space        { get; set; }
    public virtual UltimaSubscriptionEntity?    Subscription { get; set; }

    public void Configure(EntityTypeBuilder<SpaceBoostEntity> builder)
    {
        builder.HasKey(x => x.Id);

        builder.Property(x => x.UserId).IsRequired();
        builder.Property(x => x.Source).IsRequired();

        builder.HasIndex(x => x.UserId);
        builder.HasIndex(x => x.SpaceId);
        builder.HasIndex(x => new { x.UserId, x.SpaceId });
        builder.HasIndex(x => x.SubscriptionId);

        builder.HasOne(x => x.User)
           .WithMany()
           .HasForeignKey(x => x.UserId)
           .OnDelete(DeleteBehavior.Cascade);

        builder.HasOne(x => x.Space)
           .WithMany()
           .HasForeignKey(x => x.SpaceId)
           .OnDelete(DeleteBehavior.SetNull);

        builder.HasOne(x => x.Subscription)
           .WithMany(x => x.Boosts)
           .HasForeignKey(x => x.SubscriptionId)
           .OnDelete(DeleteBehavior.SetNull);
    }
}
