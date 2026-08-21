namespace Argon.Core.Entities.Data;

using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata.Builders;

/// <summary>
/// Base disposition of a privacy rule (Telegram-style). Exceptions (allow/deny lists)
/// override the mode for specific users.
/// </summary>
public enum PrivacyMode
{
    Everybody = 0,
    Contacts  = 1,
    Nobody    = 2,
}

/// <summary>
/// A single user-owned privacy rule for one <see cref="Key"/> (e.g. "stream.draw").
///
/// This is an orthogonal, extensible "about-me" policy layer (who may do X to/about me),
/// separate from the channel/role entitlement bitmask. A rule is either global
/// (<see cref="ScopeSpaceId"/> == null) or scoped to a single space; a space-scoped rule
/// takes precedence over the global one. Allow/Deny exception lists override the base
/// <see cref="Mode"/> for specific user ids (Deny beats Allow). New behaviours plug in by
/// adding a new <c>Key</c> — no schema change.
/// </summary>
public record PrivacyRuleEntity : IEntityTypeConfiguration<PrivacyRuleEntity>
{
    public const string TableName = "privacy_rule_entity";

    public Guid   Id     { get; set; }
    /// <summary>The owner the rule is about (whose preference this is).</summary>
    public Guid   UserId { get; set; }
    /// <summary>Behaviour key, e.g. <c>stream.draw</c> (see PrivacyKeys).</summary>
    public string Key    { get; set; } = null!;

    public PrivacyMode Mode { get; set; } = PrivacyMode.Everybody;

    /// <summary>Null = global rule; non-null = per-space override.</summary>
    public Guid? ScopeSpaceId { get; set; }

    /// <summary>User ids explicitly allowed regardless of <see cref="Mode"/>.</summary>
    public List<Guid> AllowExceptions { get; set; } = new();
    /// <summary>User ids explicitly denied regardless of <see cref="Mode"/> (wins over allow).</summary>
    public List<Guid> DenyExceptions { get; set; } = new();

    public DateTimeOffset CreatedAt { get; set; }
    public DateTimeOffset UpdatedAt { get; set; }

    public void Configure(EntityTypeBuilder<PrivacyRuleEntity> builder)
    {
        builder.ToTable(TableName);

        builder.HasKey(x => x.Id);

        builder.Property(x => x.Key).IsRequired().HasMaxLength(128);
        builder.Property(x => x.Mode).HasConversion<int>();

        // One rule per (owner, key, scope). Two partial-unique constraints would be ideal
        // for the nullable scope, but a composite index keeps lookups cheap and the grain
        // upserts by this tuple.
        builder.HasIndex(x => new { x.UserId, x.Key, x.ScopeSpaceId })
           .IsUnique()
           .HasDatabaseName("idx_privacy_rule_owner_key_scope");

        builder.Property(x => x.CreatedAt)
           .HasColumnType("timestamptz")
           .HasDefaultValueSql("now()")
           .ValueGeneratedOnAdd();

        builder.Property(x => x.UpdatedAt)
           .HasColumnType("timestamptz")
           .HasDefaultValueSql("now()");
    }
}
