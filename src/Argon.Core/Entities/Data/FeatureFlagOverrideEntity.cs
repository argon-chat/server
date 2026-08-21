namespace Argon.Core.Entities.Data;

using Microsoft.EntityFrameworkCore.Metadata.Builders;

/// <summary>
/// Override for a feature flag at specific scope (user/country/client).
/// Evaluation priority: User > Country > Client > Global.
/// </summary>
public record FeatureFlagOverrideEntity : ArgonEntity, IEntityTypeConfiguration<FeatureFlagOverrideEntity>
{
    /// <summary>
    /// Reference to the parent feature flag.
    /// </summary>
    public required string FeatureFlagId { get; set; }

    public FeatureFlagEntity FeatureFlag { get; set; } = null!;

    /// <summary>
    /// Override scope determining priority.
    /// </summary>
    public FeatureFlagScope Scope { get; set; }

    /// <summary>
    /// Target identifier based on scope:
    /// - User: UserId (Guid as string)
    /// - Country: ISO 3166-1 alpha-2 code (e.g., "US", "RU")
    /// - Client: Client app identifier
    /// </summary>
    public required string TargetId { get; set; }

    /// <summary>
    /// Override enabled state. Null means inherit from lower priority scope.
    /// </summary>
    public bool? Enabled { get; set; }

    /// <summary>
    /// Override rollout percentage. Null means inherit.
    /// </summary>
    public int? RolloutPercentage { get; set; }

    /// <summary>
    /// Force specific variant. Null means inherit/use default distribution.
    /// </summary>
    public string? ForcedVariant { get; set; }

    public void Configure(EntityTypeBuilder<FeatureFlagOverrideEntity> builder)
    {
        builder.HasKey(x => x.Id);
        builder.Property(x => x.FeatureFlagId).HasMaxLength(128).IsRequired();
        builder.Property(x => x.TargetId).HasMaxLength(128).IsRequired();
        builder.Property(x => x.ForcedVariant).HasMaxLength(64);

        builder.HasIndex(x => new { x.FeatureFlagId, x.Scope, x.TargetId }).IsUnique();
        builder.HasIndex(x => x.TargetId);
        builder.HasIndex(x => x.Scope);

        builder.HasOne(x => x.FeatureFlag)
           .WithMany(x => x.Overrides)
           .HasForeignKey(x => x.FeatureFlagId)
           .OnDelete(DeleteBehavior.Cascade);
    }
}
