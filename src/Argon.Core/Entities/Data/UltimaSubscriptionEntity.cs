namespace Argon.Entities;

using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata.Builders;

public record UltimaSubscriptionEntity : ArgonEntity, IEntityTypeConfiguration<UltimaSubscriptionEntity>
{
    public Guid            UserId               { get; set; }
    public UltimaTier      Tier                 { get; set; }
    public UltimaStatus    Status               { get; set; }
    public DateTimeOffset  StartsAt             { get; set; }
    public DateTimeOffset  ExpiresAt            { get; set; }
    public bool            AutoRenew            { get; set; }
    public int             BoostSlots           { get; set; } = 3;
    public DateTimeOffset? CancelledAt          { get; set; }

    [MaxLength(256)]
    public string? XsollaSubscriptionId { get; set; }

    public Guid? ActivatedFromItemId { get; set; }

    public virtual UserEntity               User   { get; set; } = null!;
    public virtual ICollection<SpaceBoostEntity> Boosts { get; set; } = new List<SpaceBoostEntity>();

    public void Configure(EntityTypeBuilder<UltimaSubscriptionEntity> builder)
    {
        builder.HasKey(x => x.Id);

        builder.Property(x => x.UserId).IsRequired();
        builder.Property(x => x.Tier).IsRequired();
        builder.Property(x => x.Status).IsRequired();
        builder.Property(x => x.StartsAt).IsRequired();
        builder.Property(x => x.ExpiresAt).IsRequired();

        builder.HasIndex(x => x.UserId);
        builder.HasIndex(x => x.Status);
        builder.HasIndex(x => x.XsollaSubscriptionId).IsUnique()
           .HasFilter("\"XsollaSubscriptionId\" IS NOT NULL");

        builder.HasOne(x => x.User)
           .WithMany()
           .HasForeignKey(x => x.UserId)
           .OnDelete(DeleteBehavior.Cascade);
    }
}
