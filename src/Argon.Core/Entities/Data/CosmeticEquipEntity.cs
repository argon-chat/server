namespace Argon.Entities;

using Microsoft.EntityFrameworkCore.Metadata.Builders;

/// <summary>
/// One thing a person has on: a catalogue item in a slot of its kind, or — for a kind with no
/// catalogue rows — the choices and tuning that are the whole of its look.
/// </summary>
/// <remarks>
/// <para><b>Hung off the person rather than off a look.</b> There is one look per person for now,
/// worn everywhere, and a table standing for it would carry nothing a person does not. When looks
/// arrive — several of them, and a different one per space — these rows move onto the default look
/// in one migration.</para>
///
/// <para><b>Not an <c>ArgonEntity</c>.</b> Taking something off deletes the row: a soft delete would
/// leave every outfit a person ever tried on behind as a live row, and nothing reads the history. So
/// there is no <c>IsDeleted</c> here for the interceptor to rewrite a delete into, and the key is the
/// slot itself — one thing per slot of a kind is what the table means.</para>
/// </remarks>
public record CosmeticEquipEntity : IEntityTypeConfiguration<CosmeticEquipEntity>
{
    public required Guid   UserId    { get; set; }
    public required string KindKey   { get; set; }
    public required int    SlotIndex { get; set; }

    /// <summary>The catalogue row worn. Null for a kind with no rows.</summary>
    public Guid? CosmeticItemId { get; set; }

    /// <summary>
    /// What was chosen on the kind's axes: axis id to option slug, as <c>CosmeticChoices</c> reads
    /// it. Null when the kind has no axes or nothing has been chosen.
    /// </summary>
    public string? Choices { get; set; }

    /// <summary>What the wearer tuned by hand, in the kind's own tuning schema.</summary>
    public string? Tuning { get; set; }

    public DateTimeOffset UpdatedAt { get; set; }

    public virtual CosmeticItemEntity? Item { get; set; }

    public void Configure(EntityTypeBuilder<CosmeticEquipEntity> builder)
    {
        builder.ToTable("CosmeticEquips");

        // Leading with the person, because a person's worn rows are only ever read as a whole.
        builder.HasKey(x => new
        {
            x.UserId,
            x.KindKey,
            x.SlotIndex
        });

        builder.Property(x => x.KindKey).HasMaxLength(64);
        builder.Property(x => x.Choices).HasColumnType("jsonb");
        builder.Property(x => x.Tuning).HasColumnType("jsonb");

        // Leaving the account takes what it was wearing with it.
        builder.HasOne<UserEntity>()
           .WithMany()
           .HasForeignKey(x => x.UserId)
           .OnDelete(DeleteBehavior.Cascade);

        // A catalogue row is soft-deleted by the console, which the global filter then hides — so a
        // worn row pointing at one is skipped on read rather than removed here. This only backstops a
        // real SQL delete.
        builder.HasOne(x => x.Item)
           .WithMany()
           .HasForeignKey(x => x.CosmeticItemId)
           .OnDelete(DeleteBehavior.Cascade);
    }
}
