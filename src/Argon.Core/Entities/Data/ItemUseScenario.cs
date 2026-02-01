namespace Argon.Api.Entities.Data;

using Microsoft.EntityFrameworkCore.Metadata.Builders;

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
           .HasValue<BoxScenario>("Box");
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