namespace Argon.Entities;

using Microsoft.EntityFrameworkCore.Metadata.Builders;

/// <summary>
/// One cosmetic worn in one loadout.
/// </summary>
public record CosmeticEquipEntity : ArgonEntity, IEntityTypeConfiguration<CosmeticEquipEntity>
{
    public required Guid LoadoutId      { get; set; }
    /// <summary>
    /// The catalogue row this is, or null for a kind that has none.
    /// </summary>
    /// <remarks>
    /// <b>Not everything worn is a thing somebody owns.</b> A nickname style is a composition — a
    /// face, a treatment, a colour, each an item of its own — plus the colours its wearer picked.
    /// There was nothing left for a style row to carry, so the two that existed were the same offer
    /// twice and choosing between them changed nothing. A kind declared bare has no rows at all and
    /// this is null on it.
    /// </remarks>
    public Guid? CosmeticItemId { get; set; }

    /// <summary>
    /// Denormalised from the item. Carried here so that enforcing the kind's stacking rule, and
    /// skipping a kind whose file was deleted, are both answerable without joining the catalogue.
    /// </summary>
    public required string KindKey { get; set; }

    /// <summary>Position within the kind. Always 0 for a kind that stacks Single.</summary>
    public int SlotIndex { get; set; }

    /// <summary>
    /// The wearer's own settings for this item — a recolourable frame's tint, or the file id for an
    /// item whose asset the wearer supplies. Null for an item worn as authored.
    /// </summary>
    public string? Overrides { get; set; }

    /// <summary>
    /// What the wearer wrote into this item, validated against the kind's content schema.
    /// </summary>
    /// <remarks>
    /// <para>Separate from <see cref="Overrides"/> on purpose. Overrides are choices from a closed
    /// list the kind declares — a face, a colour — and are checked by looking the chosen slug up.
    /// Content is <b>authored</b>: a widget's heading, the tags somebody typed. The two are validated
    /// in completely different ways and only one of them can ever contain a sentence.</para>
    ///
    /// <para>Null for every kind that has nothing to fill in, which is most of them.</para>
    /// </remarks>
    public string? Content { get; set; }

    /// <summary>
    /// Where this card sits on the profile board and how big it is, in grid cells.
    /// </summary>
    /// <remarks>
    /// <para>On the equipped row rather than on the kind, because the layout is the wearer's to
    /// choose and it runs <i>across</i> kinds — a board of one note beside two tag cards is a
    /// position no per-kind layer can express.</para>
    ///
    /// <para>Cells rather than an order and a width: a person dragging a card puts it somewhere, and
    /// an ordered list can only say what it comes after. The grid packs upwards, so a column with a
    /// gap above it closes on its own and these stay the coordinates that were dropped.</para>
    ///
    /// <para>Ignored by everything that is not a board card.</para>
    /// </remarks>
    public int BoardX { get; set; }

    public int BoardY { get; set; }

    public int BoardW { get; set; } = 1;

    public int BoardH { get; set; } = 1;

    /// <summary>When the item takes itself off. Null is until the wearer changes it.</summary>
    public DateTimeOffset? ExpiresAt { get; set; }

    public virtual CosmeticLoadoutEntity Loadout { get; set; } = null!;
    public virtual CosmeticItemEntity    Item    { get; set; } = null!;

    public void Configure(EntityTypeBuilder<CosmeticEquipEntity> builder)
    {
        builder.Property(x => x.KindKey).HasMaxLength(64);
        builder.Property(x => x.BoardW).HasDefaultValue(1);
        builder.Property(x => x.BoardH).HasDefaultValue(1);

        builder.HasOne(x => x.Loadout)
           .WithMany(x => x.Equips)
           .HasForeignKey(x => x.LoadoutId)
           .OnDelete(DeleteBehavior.Cascade);

        // Restrict rather than cascade: deleting a catalogue row out from under everyone wearing it
        // is an operator mistake, and it should fail loudly rather than undress people.
        builder.HasOne(x => x.Item)
           .WithMany()
           .HasForeignKey(x => x.CosmeticItemId)
           .OnDelete(DeleteBehavior.Restrict);

        builder.HasIndex(x => new
            {
                x.LoadoutId,
                x.KindKey,
                x.SlotIndex
            })
           .IsUnique()
           .HasFilter("\"IsDeleted\" = false");
    }
}
