namespace Argon.Entities;

using Microsoft.EntityFrameworkCore.Metadata.Builders;

/// <summary>
/// How a person came to own a cosmetic. Recorded because revocation, support and refunds all need to
/// know, and the inventory row alone does not say.
/// </summary>
public enum CosmeticOwnershipSource
{
    OperatorGrant,
    PromoCode,
    Purchase,
    Gift,

    /// <summary>
    /// The grant arrived through the item machinery — a key from a case, from a code, or gifted.
    /// </summary>
    /// <remarks>
    /// One member rather than separate <c>Box</c>/<c>Code</c> values, because where the key itself
    /// came from is a question about the item, and the answer is stored on the item.
    /// <see cref="CosmeticOwnershipEntity.InventoryItemId"/> says which item, and the chain is read
    /// from there. Two records of the same history would drift apart at the first gift.
    /// </remarks>
    Item
}

/// <summary>
/// One person's claim on one catalogue item.
/// </summary>
/// <remarks>
/// Subscription-included items deliberately have no row here. An item acquired by tier is owned for
/// as long as the subscription is active and not one moment longer, and that is a question about the
/// subscription, not about a row somebody has to remember to delete. It is also what makes a lapse
/// free: nothing is written when a subscription ends, and nothing has to be restored when it renews.
/// </remarks>
public record CosmeticOwnershipEntity : ArgonEntity, IEntityTypeConfiguration<CosmeticOwnershipEntity>
{
    public required Guid UserId         { get; set; }
    public required Guid CosmeticItemId { get; set; }

    public CosmeticOwnershipSource Source { get; set; } = CosmeticOwnershipSource.OperatorGrant;

    /// <summary>The inventory row this grant minted, when it came through the item machinery.</summary>
    public Guid? InventoryItemId { get; set; }

    public Guid? GiftedByUserId { get; set; }

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
