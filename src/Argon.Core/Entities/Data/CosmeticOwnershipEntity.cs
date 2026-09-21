namespace Argon.Entities;

using Microsoft.EntityFrameworkCore.Metadata.Builders;

/// <summary>
/// How a person came to own a cosmetic. Recorded because revocation, support and refunds all need
/// to know, and an inventory row alone does not say.
/// </summary>
/// <remarks>
/// Only the ways this build can actually grant one. A member that is declared and never assigned is
/// worse than a missing member: a reader trusting it gets a value that was never written.
/// </remarks>
public enum CosmeticOwnershipSource
{
    OperatorGrant
}

/// <summary>
/// One person's claim on one catalogue item.
/// </summary>
/// <remarks>
/// Subscription-included items deliberately have no row here. An item acquired by tier is owned for
/// as long as the subscription is active and not one moment longer, and that is a question about
/// the subscription, not about a row somebody has to remember to delete. It is also what makes a
/// lapse free: nothing is written when a subscription ends, and nothing has to be restored when it
/// renews.
/// </remarks>
public record CosmeticOwnershipEntity : ArgonEntity, IEntityTypeConfiguration<CosmeticOwnershipEntity>
{
    public required Guid UserId         { get; set; }
    public required Guid CosmeticItemId { get; set; }

    public CosmeticOwnershipSource Source { get; set; } = CosmeticOwnershipSource.OperatorGrant;

    /// <summary>When the claim lapses on its own. Null is permanent.</summary>
    public DateTimeOffset? ExpiresAt { get; set; }

    /// <summary>
    /// Set instead of deleting the row, so that a refund or a moderation action leaves a record of
    /// what was taken and when.
    /// </summary>
    public DateTimeOffset? RevokedAt { get; set; }

    public virtual UserEntity         User { get; set; } = null!;
    public virtual CosmeticItemEntity Item { get; set; } = null!;

    public bool IsActiveAt(DateTimeOffset moment)
        => RevokedAt is null && (ExpiresAt is null || ExpiresAt > moment);

    public void Configure(EntityTypeBuilder<CosmeticOwnershipEntity> builder)
    {
        builder.HasOne(x => x.User)
           .WithMany()
           .HasForeignKey(x => x.UserId)
           .OnDelete(DeleteBehavior.Cascade);

        builder.HasOne(x => x.Item)
           .WithMany()
           .HasForeignKey(x => x.CosmeticItemId)
           .OnDelete(DeleteBehavior.Cascade);

        builder.HasIndex(x => new
            {
                x.UserId,
                x.CosmeticItemId
            })
           .IsUnique()
           .HasFilter("\"RevokedAt\" IS NULL AND \"IsDeleted\" = false");

        builder.HasIndex(x => x.ExpiresAt)
           .HasFilter("\"ExpiresAt\" IS NOT NULL AND \"RevokedAt\" IS NULL");
    }
}
