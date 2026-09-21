namespace Argon.Entities;

using Microsoft.EntityFrameworkCore.Metadata.Builders;

/// <summary>
/// How an item may be come by. Flags, because the same cosmetic is often both grantable by an
/// operator and included with a subscription.
/// </summary>
/// <remarks>
/// Bit positions are stable. The ways of coming by a cosmetic that need machinery this build has
/// not got — a promo code, a purchase, a gift, a key out of a case — are left out rather than
/// declared and never set, and slot in at their own bits when that machinery lands.
/// </remarks>
[Flags]
public enum CosmeticAcquisitionMode
{
    None          = 0,
    OperatorGrant = 1 << 0,
    UltimaTier    = 1 << 2,

    /// <summary>
    /// Everybody has it, with no row behind it.
    /// </summary>
    /// <remarks>
    /// Needed because entitlement is declared per kind and paid-ness is per item: an axis of colours
    /// where most are free and a few are not cannot be expressed by the kind alone. Checked ahead of
    /// ownership, so a free item costs no lookup.
    /// </remarks>
    Free          = 1 << 5
}

/// <summary>
/// One publishable cosmetic: what kind it is, what it looks like, and how it is come by.
/// </summary>
public record CosmeticItemEntity : ArgonEntity, IEntityTypeConfiguration<CosmeticItemEntity>
{
    /// <summary>
    /// The <c>ICosmeticKind</c> key this row belongs to. A row whose key is not in the registry is
    /// an orphan: never served, never equippable, and never deleted on its own.
    /// </summary>
    public required string KindKey { get; set; }

    /// <summary>
    /// Stable handle within the kind, unique per kind. It is what a client matches a code-backed
    /// option to its own file by, so it is a wire value and renaming it is a migration.
    /// </summary>
    public required string Slug { get; set; }

    public required string  NameKey        { get; set; }
    public          string? DescriptionKey { get; set; }
    public          string? Rarity         { get; set; }
    public          int     SortOrder      { get; set; }

    /// <summary>
    /// Bumped whenever the payload or assets change, so a client can tell a re-authored cosmetic
    /// from the one it cached.
    /// </summary>
    public int Version { get; set; } = 1;

    /// <summary>JSON for the kind's payload schema. Validated against it before publication.</summary>
    public string Payload { get; set; } = "{}";

    /// <summary>
    /// Asset slot name to file id. The key is <c>CosmeticAssetSlots.KeyOf</c>, which is also how a
    /// payload names the slot it draws.
    /// </summary>
    public Dictionary<string, string> AssetFileIds { get; set; } = new();

    /// <summary>
    /// Whether the row is servable at all. The operator-facing kill switch for one cosmetic; the
    /// kind-wide one is a feature flag.
    /// </summary>
    public bool IsEnabled { get; set; } = true;

    public bool            IsPublished { get; set; }
    public DateTimeOffset? PublishedAt { get; set; }

    /// <summary>Window during which the item may be acquired. Null on either side means unbounded.</summary>
    public DateTimeOffset? AvailableFrom  { get; set; }
    public DateTimeOffset? AvailableUntil { get; set; }

    public CosmeticAcquisitionMode AcquisitionMode { get; set; } = CosmeticAcquisitionMode.OperatorGrant;

    /// <summary>
    /// Which subscription the item comes with, when <see cref="AcquisitionMode"/> includes
    /// <see cref="CosmeticAcquisitionMode.UltimaTier"/>. Null means any active subscription, which
    /// is what the product supports today — <c>UltimaTier</c> is a billing period, not a ladder.
    /// </summary>
    public UltimaTier? UltimaTierRequired { get; set; }

    public void Configure(EntityTypeBuilder<CosmeticItemEntity> builder)
    {
        builder.Property(x => x.KindKey).HasMaxLength(64);
        builder.Property(x => x.Slug).HasMaxLength(128);
        builder.Property(x => x.NameKey).HasMaxLength(128);
        builder.Property(x => x.DescriptionKey).HasMaxLength(128);
        builder.Property(x => x.Rarity).HasMaxLength(32);

        builder.Property(x => x.AssetFileIds)
           .HasColumnType("jsonb")
           .HasConversion(
                v => JsonConvert.SerializeObject(v),
                v => JsonConvert.DeserializeObject<Dictionary<string, string>>(v) ?? new Dictionary<string, string>()
            )
           .Metadata.SetValueComparer(new JsonValueComparer<Dictionary<string, string>>());

        builder.HasIndex(x => new
        {
            x.KindKey,
            x.IsPublished,
            x.IsEnabled
        });

        builder.HasIndex(x => new
            {
                x.KindKey,
                x.Slug
            })
           .IsUnique()
           .HasFilter("\"IsDeleted\" = false");

        // The sweep that retires an item when its window closes reads exactly this pair.
        builder.HasIndex(x => new
        {
            x.IsPublished,
            x.AvailableUntil
        });
    }
}
