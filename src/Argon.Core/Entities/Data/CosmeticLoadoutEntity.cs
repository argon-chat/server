namespace Argon.Entities;

using Microsoft.EntityFrameworkCore.Metadata.Builders;

/// <summary>
/// A named persona: what somebody is called, what they look like, and what they are wearing, in the
/// spaces this look is assigned to.
/// </summary>
/// <remarks>
/// <para>This is the shape that beats a per-guild profile. The persona exists on its own and is
/// pointed at however many spaces the person likes, so changing a look once changes it everywhere it
/// is worn rather than in each space separately.</para>
///
/// <para><b>A look is a diff, not a copy.</b> Each identity field is null until somebody overrides
/// it, and null means the account's own value shows through. That is what keeps a look cheap to make
/// and keeps the account the single place to change what everything else inherits — copying the
/// identity into every look would mean a new avatar had to be set once per look forever after.</para>
/// </remarks>
public record CosmeticLoadoutEntity : ArgonEntity, IEntityTypeConfiguration<CosmeticLoadoutEntity>
{
    public required Guid   UserId { get; set; }
    public required string Name   { get; set; }

    public bool IsDefault { get; set; }
    public int  SortOrder { get; set; }

    /// <summary>
    /// Set aside: worn nowhere, whatever it is assigned to.
    /// </summary>
    /// <remarks>
    /// <para>Because "not today" is a thing people want and the assignments could not say it. The
    /// only way to stop wearing a look was to take every space off it one at a time, and then to put
    /// them all back the next day — so the spaces were being used as an on switch, and lost every
    /// time it was turned off.</para>
    ///
    /// <para>It is the look that is set aside, not the spaces: a space on a look that is put away
    /// falls through to whatever is worn everywhere, exactly as if the look had never claimed it, and
    /// gets it back untouched when the look comes out again.</para>
    /// </remarks>
    public bool IsPaused { get; set; }

    /// <summary>
    /// What this look is called in the spaces it applies to. Null wears the account's display name.
    /// </summary>
    /// <remarks>
    /// Held to the same length the account's own name is, and to the same cooldown: a look whose name
    /// could be changed freely would be the account's cooldown with an extra step.
    /// </remarks>
    public string? DisplayNameOverride { get; set; }

    public DateTimeOffset? DisplayNameChangedAt { get; set; }

    /// <summary>The look's own picture. Null wears the account's.</summary>
    /// <remarks>
    /// Uploaded through the same path as an account avatar and moderated by the same call — a
    /// picture seen by other people is a picture seen by other people, whichever field holds it.
    /// </remarks>
    public string? AvatarFileIdOverride { get; set; }

    /// <summary>What the profile says about this persona. Null wears the account's bio.</summary>
    public string? BioOverride { get; set; }

    public virtual UserEntity                       User   { get; set; } = null!;
    public virtual ICollection<CosmeticEquipEntity> Equips { get; set; } = new List<CosmeticEquipEntity>();

    public void Configure(EntityTypeBuilder<CosmeticLoadoutEntity> builder)
    {
        builder.Property(x => x.Name).HasMaxLength(64);
        builder.Property(x => x.DisplayNameOverride).HasMaxLength(64);
        builder.Property(x => x.AvatarFileIdOverride).HasMaxLength(64);
        builder.Property(x => x.BioOverride).HasMaxLength(512);

        builder.HasOne(x => x.User)
           .WithMany()
           .HasForeignKey(x => x.UserId)
           .OnDelete(DeleteBehavior.Cascade);

        builder.HasIndex(x => new
            {
                x.UserId,
                x.Name
            })
           .IsUnique()
           .HasFilter("\"IsDeleted\" = false");

        // Exactly one default per person. Enforced here rather than in code because the fallback
        // path reads it expecting one row, and a second would turn every profile read into a throw.
        builder.HasIndex(x => x.UserId)
           .IsUnique()
           .HasFilter("\"IsDefault\" = true AND \"IsDeleted\" = false");
    }
}
