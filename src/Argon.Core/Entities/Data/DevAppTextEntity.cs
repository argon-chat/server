namespace Argon.Core.Entities.Data;

using Microsoft.EntityFrameworkCore.Metadata.Builders;

/// <summary>
/// One localized string of an application: a key (<c>motd</c>, <c>description</c>, see
/// <c>AppTextKeys</c>) in one locale, spelled the way the client spells it (<c>en</c>, <c>jp</c>,
/// <c>ru_pt</c>).
/// </summary>
public class DevAppTextEntity : IEntityTypeConfiguration<DevAppTextEntity>
{
    public         Guid         AppId { get; set; }
    public virtual DevAppEntity App   { get; set; } = null!;

    public required string Key    { get; set; }
    public required string Locale { get; set; }
    public required string Value  { get; set; }

    public void Configure(EntityTypeBuilder<DevAppTextEntity> builder)
    {
        builder.ToTable("DevAppTexts");
        builder.HasKey(x => new
        {
            x.AppId,
            x.Key,
            x.Locale
        });

        builder.Property(x => x.Key).HasMaxLength(64);
        builder.Property(x => x.Locale).HasMaxLength(16);

        builder.HasOne(x => x.App)
           .WithMany(x => x.Texts)
           .HasForeignKey(x => x.AppId)
           .OnDelete(DeleteBehavior.Cascade);
    }
}
