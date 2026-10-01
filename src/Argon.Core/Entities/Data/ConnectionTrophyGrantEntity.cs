namespace Argon.Entities;

using Microsoft.EntityFrameworkCore.Metadata.Builders;

/// <summary>
/// A trophy paid out for an external identity — the GitHub contributor coin, keyed by the GitHub
/// account rather than by the Argon account.
/// </summary>
/// <remarks>
/// Relinking the same GitHub account to a second Argon account must not mint a second coin, so the
/// key is the external identity and the row outlives both the connection and the user: no foreign
/// key, no cascade. It holds our own user id and a timestamp, nothing about the person.
/// </remarks>
public record ConnectionTrophyGrantEntity : IEntityTypeConfiguration<ConnectionTrophyGrantEntity>
{
    public ConnectionProvider Provider { get; set; }

    [MaxLength(128)] public required string ExternalId { get; set; }
    [MaxLength(128)] public required string TrophyId   { get; set; }

    public Guid           UserId    { get; set; }
    public DateTimeOffset GrantedAt { get; set; }

    public void Configure(EntityTypeBuilder<ConnectionTrophyGrantEntity> builder)
    {
        builder.HasKey(x => new { x.Provider, x.ExternalId, x.TrophyId });
        builder.HasIndex(x => x.UserId);
    }
}
