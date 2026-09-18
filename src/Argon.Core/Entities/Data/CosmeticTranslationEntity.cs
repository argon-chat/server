namespace Argon.Entities;

using Microsoft.EntityFrameworkCore.Metadata.Builders;

/// <summary>
/// What one cosmetic is called in one language.
/// </summary>
/// <remarks>
/// <para>A row rather than a key into the client's locale files, because a cosmetic is created in
/// the console and a key is created in a release: the old arrangement meant every new cosmetic was
/// nameless in every language until a client shipped, and silently so — vue-i18n is configured with
/// <c>missingWarn: false</c>, so an unknown key renders as itself.</para>
///
/// <para><c>CosmeticItemEntity.NameKey</c> stays and is still served, as the last resort behind this
/// table — but it is not a display name any more, and nothing outside depends on its value. The
/// column with a wire guarantee is <c>Slug</c>, which is what a badge projects onto the legacy
/// <c>badges</c> array as.</para>
/// </remarks>
public record CosmeticTranslationEntity : ArgonEntity, IEntityTypeConfiguration<CosmeticTranslationEntity>
{
    public required Guid CosmeticItemId { get; set; }

    /// <summary>
    /// The locale code as the client app spells it — <c>en</c>, <c>ru</c>, <c>jp</c>, <c>am</c>,
    /// <c>ru_pt</c> — not BCP-47. Validated for shape only, never against a list: a language the
    /// client gains next release should be translatable today, and a row nobody reads costs nothing.
    /// </summary>
    public required string Locale { get; set; }

    public required string  Name        { get; set; }
    public          string? Description { get; set; }

    public virtual CosmeticItemEntity Item { get; set; } = null!;

    public void Configure(EntityTypeBuilder<CosmeticTranslationEntity> builder)
    {
        builder.Property(x => x.Locale).HasMaxLength(16);
        builder.Property(x => x.Name).HasMaxLength(128);
        builder.Property(x => x.Description).HasMaxLength(512);

        // Backstops a real SQL delete, which is not how a cosmetic is normally deleted: the
        // soft-delete interceptor turns the console's Remove into an update, so an item going away
        // leaves its names behind as live rows. Nothing serves them, because every read starts from
        // the item.
        builder.HasOne(x => x.Item)
           .WithMany()
           .HasForeignKey(x => x.CosmeticItemId)
           .OnDelete(DeleteBehavior.Cascade);

        builder.HasIndex(x => new
            {
                x.CosmeticItemId,
                x.Locale
            })
           .IsUnique()
           .HasFilter("\"IsDeleted\" = false");
    }
}
