namespace Argon.Entities;

using Microsoft.EntityFrameworkCore.Metadata.Builders;

/// <summary>
/// How an item may be come by. Flags, because the same cosmetic is often both grantable by an
/// operator and included with a subscription.
/// </summary>
[Flags]
public enum CosmeticAcquisitionMode
{
    None          = 0,
    OperatorGrant = 1 << 0,
    PromoCode     = 1 << 1,
    UltimaTier    = 1 << 2,
    Purchase      = 1 << 3,
    Gift          = 1 << 4,

    /// <summary>
    /// Everybody has it, with no row behind it.
    /// </summary>
    /// <remarks>
    /// Needed because entitlement is declared per kind and paid-ness is per item: an axis of colours
    /// where most are free and a few are not cannot be expressed by the kind alone. Checked ahead of
    /// ownership, so a free item costs no lookup.
    /// </remarks>
    Free          = 1 << 5,

    /// <summary>
    /// The item is obtained by a key falling out of a case.
    /// </summary>
    /// <remarks>
    /// Gates nothing — the scenario on the key is what actually grants the item; this flag only says
    /// how it is offered. Deliberate: a key pointing at a cosmetic that does not carry this flag still
    /// works, and the mismatch is something an operator sees in the console rather than a refusal in a
    /// player's way.
    /// </remarks>
    Drop          = 1 << 6
}

/// <summary>
/// Who supplies the pixels: the catalogue row, or the person wearing it.
/// </summary>
public enum CosmeticAssetSource
{
    /// <summary>The asset is uploaded once by an operator and shared by everyone who equips it.</summary>
    Catalogue,

    /// <summary>
    /// The row is a frame for the wearer's own file, whose id lives in
    /// <c>CosmeticEquipEntity.Overrides</c>. A profile banner is the example.
    /// </summary>
    UserProvided
}

/// <summary>
/// One publishable cosmetic: what kind it is, what it looks like, and how it is come by.
/// </summary>
public record CosmeticItemEntity : ArgonEntity, IEntityTypeConfiguration<CosmeticItemEntity>
{
    /// <summary>
    /// The <c>ICosmeticKind</c> key this row belongs to. A row whose key is not in the registry is an
    /// orphan: never served, never equippable, and never deleted on its own.
    /// </summary>
    public required string KindKey { get; set; }

    /// <summary>
    /// Stable handle within the kind, unique per kind. This is what a badge projects onto the legacy
    /// <c>badges</c> array as, so it is a wire value and renaming it is a migration.
    /// </summary>
    public required string Slug { get; set; }

    public required string  NameKey        { get; set; }
    public          string? DescriptionKey { get; set; }
    public          string? Rarity         { get; set; }
    public          int     SortOrder      { get; set; }

    /// <summary>
    /// Bumped whenever the payload or assets change, so a client can tell a re-authored cosmetic from
    /// the one it cached.
    /// </summary>
    public int Version { get; set; } = 1;

    /// <summary>JSON for the kind's payload type. Validated against it before publication.</summary>
    public string Payload { get; set; } = "{}";

    /// <summary>Asset slot name to file id. Slot names are <c>CosmeticAssetSlot</c> members.</summary>
    public Dictionary<string, string> AssetFileIds { get; set; } = new();

    public CosmeticAssetSource AssetSource { get; set; } = CosmeticAssetSource.Catalogue;

    /// <summary>
    /// Whether the row is servable at all. The operator-facing kill switch for one cosmetic; the
    /// kind-wide one is a feature flag.
    /// </summary>
    public bool IsEnabled { get; set; } = true;

    public bool            IsPublished { get; set; }
    public DateTimeOffset? PublishedAt { get; set; }

    /// <summary>
    /// When this row's files went into a client build, and which build.
    /// </summary>
    /// <remarks>
    /// <b>A record of something that already happened, not an instruction.</b> The order is export,
    /// release the client, then set this. Setting it first makes the console claim people have
    /// bytes they do not have.
    /// <para>
    /// The server's own behaviour does not change: it keeps sending the same file ids, because it
    /// knows nothing about which build anybody is running. What changes is what an operator is
    /// allowed to do to the row — see <c>AdminConsoleImpl.Cosmetics</c>. Deleting it, switching it
    /// off, unpublishing it or giving it an availability window are all refused, because the files
    /// are in builds that are out there and none of those actions can reach them.
    /// </para>
    /// <para>
    /// Replacing an asset stays allowed. It mints a new file id, every pack misses on it and every
    /// client quietly returns to the CDN until a later release exports the new bytes — so a typo in
    /// a picture costs a fallback rather than a client release.
    /// </para>
    /// </remarks>
    public DateTimeOffset? ShippedInClientAt    { get; set; }
    public string?         ShippedInClientBuild { get; set; }

    /// <summary>Window during which the item may be acquired. Null on either side means unbounded.</summary>
    public DateTimeOffset? AvailableFrom  { get; set; }
    public DateTimeOffset? AvailableUntil { get; set; }

    public CosmeticAcquisitionMode AcquisitionMode { get; set; } = CosmeticAcquisitionMode.OperatorGrant;

    /// <summary>
    /// Which subscription the item comes with, when <see cref="AcquisitionMode"/> includes
    /// <see cref="CosmeticAcquisitionMode.UltimaTier"/>. Null means any active subscription, which is
    /// what the product supports today — <c>UltimaTier</c> is a billing period, not a ladder.
    /// </summary>
    public UltimaTier? UltimaTierRequired { get; set; }

    /// <summary>
    /// The external catalogue SKU this item is sold as. Unused at launch: it exists so that turning
    /// payments on later is a column being filled rather than a table being reshaped.
    /// </summary>
    public string? PriceSku { get; set; }

    /// <summary>
    /// The inventory template a grant of this cosmetic mints, tying it to the existing item, coupon
    /// and gift machinery rather than growing a second one.
    /// </summary>
    public string? GrantItemTemplateId { get; set; }

    /// <summary>
    /// The pre-cosmetics integer this item answers to for clients that predate the system. Set only
    /// for the handful of items that existed as hardcoded presets.
    /// </summary>
    public int? LegacyId { get; set; }

    /// <summary>
    /// How many of this card one board may hold. One makes it unique; null leaves it at the kind's
    /// own ceiling.
    /// </summary>
    /// <remarks>
    /// <para>On the row rather than in the kind file because it is a decision and not a capability.
    /// What a card <i>can</i> do — how wide it may be drawn, how short it may be made — is the
    /// component's business and belongs in code. Whether this particular offering may be had twice is
    /// an operator's to change on a Tuesday, and nothing about it needs a release.</para>
    ///
    /// <para>Held to the kind's ceiling wherever it is read: a row cannot offer more of itself than
    /// the code is prepared to draw.</para>
    /// </remarks>
    public int? MaxPerBoard { get; set; }

    /// <summary>The size a card of this row is created at. Null takes the kind's smallest.</summary>
    public int? BoardDefaultW { get; set; }

    public int? BoardDefaultH { get; set; }

    public void Configure(EntityTypeBuilder<CosmeticItemEntity> builder)
    {
        builder.Property(x => x.KindKey).HasMaxLength(64);
        builder.Property(x => x.Slug).HasMaxLength(128);
        builder.Property(x => x.NameKey).HasMaxLength(128);
        builder.Property(x => x.DescriptionKey).HasMaxLength(128);
        builder.Property(x => x.Rarity).HasMaxLength(32);
        builder.Property(x => x.PriceSku).HasMaxLength(128);
        builder.Property(x => x.GrantItemTemplateId).HasMaxLength(255);
        builder.Property(x => x.ShippedInClientBuild).HasMaxLength(64);

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

        // A pre-cosmetics client sends an integer, and the write path turns it back into an item.
        // Unique because two items of one kind answering to the same legacy id would make that
        // translation a coin toss.
        builder.HasIndex(x => new
            {
                x.KindKey,
                x.LegacyId
            })
           .IsUnique()
           .HasFilter("\"LegacyId\" IS NOT NULL AND \"IsDeleted\" = false");
    }
}
