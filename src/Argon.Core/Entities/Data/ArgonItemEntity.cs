namespace Argon.Api.Entities.Data;

using Microsoft.EntityFrameworkCore.Metadata.Builders;

public class ArgonCouponRedemptionEntity : IEntityTypeConfiguration<ArgonCouponRedemptionEntity>
{
    public Guid              Id         { get; set; }
    public Guid              CouponId   { get; set; }
    public ArgonCouponEntity Coupon     { get; set; } = null!;
    public Guid              UserId     { get; set; }
    public DateTimeOffset    RedeemedAt { get; set; }

    public ICollection<ArgonItemEntity> Items { get; set; } = new List<ArgonItemEntity>();

    public void Configure(EntityTypeBuilder<ArgonCouponRedemptionEntity> builder)
    {
        builder.HasKey(x => x.Id);

        builder.Property(x => x.UserId).IsRequired();
        builder.Property(x => x.RedeemedAt).IsRequired();

        builder.HasOne(x => x.Coupon)
           .WithMany(x => x.Redemptions)
           .HasForeignKey(x => x.CouponId)
           .OnDelete(DeleteBehavior.Cascade);

        builder.HasMany(x => x.Items)
           .WithOne()
           .HasForeignKey(i => i.RedemptionId)
           .OnDelete(DeleteBehavior.Cascade);
    }
}

public record ArgonItemEntity : ArgonEntity, IMapper<ArgonItemEntity, InventoryItem>, IEntityTypeConfiguration<ArgonItemEntity>
{
    public required string         TemplateId    { get; set; }
    public          bool           IsUsable      { get; set; }
    public          bool           IsGiftable    { get; set; }
    public          ItemUseVector? UseVector     { get; set; }
    public          Guid?          ReceivedFrom  { get; set; }
    public          Guid           OwnerId       { get; set; }
    public          bool           IsAffectBadge { get; set; }
    public          TimeSpan?      TTL           { get; set; }
    public          Guid?          RedemptionId  { get; set; }
    public          bool           IsReference   { get; set; }

    public Guid?            ScenarioKey { get; set; }
    public ItemUseScenario? Scenario    { get; set; }

    public static InventoryItem Map(scoped in ArgonItemEntity self)
        => new(self.TemplateId, self.Id, self.CreatedAt.UtcDateTime, self.IsUsable, self.IsGiftable, self.UseVector,
            self.ReceivedFrom, self.TTL);

    public void Configure(EntityTypeBuilder<ArgonItemEntity> builder)
    {
        builder.HasKey(x => x.Id);
        builder.HasIndex(x => x.TemplateId);
        builder.HasIndex(x => x.OwnerId);
        builder.Property(x => x.TemplateId).HasMaxLength(255);
        builder.HasIndex(x => x.IsReference);
        builder.HasIndex(x => new
        {
            x.OwnerId,
            x.TemplateId
        });

        builder.HasIndex(x => new
        {
            x.OwnerId,
            x.IsAffectBadge
        });

        builder.HasIndex(x => new
        {
            x.Id,
            x.OwnerId
        });

        builder.HasIndex(x => new {
            x.Id,
            x.IsReference
        });

        builder.HasOne(i => i.Scenario)
           .WithMany()
           .HasForeignKey(i => i.ScenarioKey)
           .OnDelete(DeleteBehavior.SetNull);
    }
}