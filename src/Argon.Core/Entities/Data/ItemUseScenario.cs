namespace Argon.Api.Entities.Data;

using Microsoft.EntityFrameworkCore.Metadata.Builders;
using System.ComponentModel.DataAnnotations.Schema;

public abstract record ItemUseScenario : IEntityTypeConfiguration<ItemUseScenario>
{
    public Guid Key { get; set; }

    public void Configure(EntityTypeBuilder<ItemUseScenario> builder)
    {
        builder.HasKey(x => x.Key);
        builder.UseTphMappingStrategy();

        builder.HasDiscriminator<string>("ScenarioType")
           .HasValue<RedeemScenario>("RedeemCode")
           .HasValue<PremiumScenario>("Premium")
           .HasValue<QualifierBox>("QualifierBox")
           .HasValue<MultipleQualifierBox>("MultipleQualifierBox")
           .HasValue<BoxScenario>("Box")
           .HasValue<CosmeticScenario>("Cosmetic");
    }
}

public record RedeemScenario : ItemUseScenario
{
    public required string Code       { get; set; }
    public required string ServiceKey { get; set; }
}

public record PremiumScenario : ItemUseScenario
{
    public required string PlanId { get; set; }

    // Pinned rather than left to convention: CosmeticScenario below also declares a DurationDays
    // property in this same TPH table, and EF's own collision handling renamed this one — the one
    // that already holds data — instead of the new one. The pin is what stops that from recurring.
    [Column("DurationDays")]
    public int DurationDays { get; set; }

    [MaxLength(256)]
    public string? GiftMessage { get; set; }
}

public record QualifierBox : ItemUseScenario
{
    public         Guid             ReferenceItemId { get; set; }
    public virtual ArgonItemEntity? ReferenceItem   { get; set; }
}

public record MultipleQualifierBox : ItemUseScenario
{
    public virtual ICollection<Guid>             ReferenceItemIds { get; set; } = [];
    public virtual ICollection<ArgonItemEntity>? ReferenceItems   { get; set; }
}

public record BoxScenario : ItemUseScenario
{
    public string Edition { get; set; }
}

/// <summary>
/// A key: an item that is exchanged for ownership of a cosmetic when used. The link lives here
/// rather than on the catalogue row, for the same reason every other scenario carries its own data —
/// a scenario <i>is</i> "what happens when this item is used". <see cref="CosmeticItemEntity.GrantItemTemplateId"/>
/// stays as a back-reference for the admin console, but the authority is here.
/// </summary>
public record CosmeticScenario : ItemUseScenario
{
    public required Guid CosmeticId { get; set; }

    /// <summary>
    /// Empty means the ownership is permanent; a number means that many days counted from the moment
    /// of use, not from when the item was received — an item sat on in an inventory for a month should
    /// still grant its full window.
    /// </summary>
    // Pinned alongside PremiumScenario.DurationDays above: same TPH table, same property name by
    // design (see there), and left to EF's own disambiguation one of the two columns gets silently
    // renamed. Pinning both means the mapping is stated, not inferred.
    [Column("CosmeticDurationDays")]
    public int? DurationDays { get; set; }
}
