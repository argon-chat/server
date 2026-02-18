namespace Argon.Core.Entities.Data;

using Microsoft.EntityFrameworkCore.Metadata.Builders;

/// <summary>
/// Override scope priority (higher value = higher priority).
/// </summary>
public enum FeatureFlagScope
{
    Global  = 0,
    Client  = 10,
    Country = 20,
    User    = 30
}

/// <summary>
/// Base feature flag definition with default state and optional A/B variants.
/// </summary>
public record FeatureFlagEntity : ArgonEntity<string>, IEntityTypeConfiguration<FeatureFlagEntity>
{
    /// <summary>
    /// Human-readable description.
    /// </summary>
    public string? Description { get; set; }

    /// <summary>
    /// Default enabled state when no overrides match.
    /// </summary>
    public bool DefaultEnabled { get; set; }

    /// <summary>
    /// Percentage of users to enable (0-100) for gradual rollout.
    /// Null means use DefaultEnabled without percentage logic.
    /// </summary>
    public int? RolloutPercentage { get; set; }

    /// <summary>
    /// JSON-serialized variants for A/B testing (e.g., {"control": 50, "variant_a": 30, "variant_b": 20}).
    /// Null for simple on/off flags.
    /// </summary>
    public string? Variants { get; set; }

    /// <summary>
    /// Optional expiration date for time-limited experiments.
    /// </summary>
    public DateTimeOffset? ExpiresAt { get; set; }

    public ICollection<FeatureFlagOverrideEntity> Overrides { get; set; } = [];

    public void Configure(EntityTypeBuilder<FeatureFlagEntity> builder)
    {
        builder.HasKey(x => x.Id);
        builder.Property(x => x.Id).HasMaxLength(128);
        builder.Property(x => x.Description).HasMaxLength(512);
        builder.Property(x => x.Variants).HasMaxLength(2048);

        builder.HasIndex(x => x.DefaultEnabled);
        builder.HasIndex(x => x.ExpiresAt);
    }
}
