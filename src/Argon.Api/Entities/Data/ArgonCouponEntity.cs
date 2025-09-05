namespace Argon.Api.Entities.Data;

using Microsoft.EntityFrameworkCore.Metadata.Builders;

public record ArgonCouponEntity : ArgonEntity, IEntityTypeConfiguration<ArgonCouponEntity>
{
    public         string           Code                  { get; set; } = null!;
    public         string?          Description           { get; set; }
    public         DateTimeOffset   ValidFrom             { get; set; }
    public         DateTimeOffset   ValidTo               { get; set; }
    public         int              MaxRedemptions        { get; set; }
    public         int              RedemptionCount       { get; set; }
    public         bool             IsActive              { get; set; }
    public         Guid?            ReferenceItemEntityId { get; set; }
    public virtual ArgonItemEntity? ReferenceItemEntity   { get; set; }

    public ICollection<ArgonCouponRedemptionEntity> Redemptions { get; set; } = new List<ArgonCouponRedemptionEntity>();

    public void Configure(EntityTypeBuilder<ArgonCouponEntity> builder)
    {
        builder.HasKey(x => x.Id);

        builder.HasKey(x => x.Id);

        builder.HasIndex(x => x.Code).IsUnique();

        builder.Property(x => x.Code)
           .IsRequired()
           .HasMaxLength(64);

        builder.Property(x => x.Description)
           .HasMaxLength(512);

        builder.Property(x => x.ValidFrom).IsRequired();
        builder.Property(x => x.ValidTo).IsRequired();
        builder.Property(x => x.MaxRedemptions).IsRequired();
        builder.Property(x => x.RedemptionCount).IsRequired();
        builder.Property(x => x.IsActive).IsRequired();

        builder.HasOne(x => x.ReferenceItemEntity)
           .WithMany()
           .HasForeignKey(x => x.ReferenceItemEntityId)
           .OnDelete(DeleteBehavior.SetNull);

        builder.HasMany(x => x.Redemptions)
           .WithOne(x => x.Coupon)
           .HasForeignKey(x => x.CouponId);
    }
}