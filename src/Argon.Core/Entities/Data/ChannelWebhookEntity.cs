namespace Argon.Entities;

using Microsoft.EntityFrameworkCore.Metadata.Builders;
using Orleans.Concurrency;

/// <summary>An incoming webhook of a text or announcement channel. Only a hash of its token is kept.</summary>
public record ChannelWebhookEntity : IEntityTypeConfiguration<ChannelWebhookEntity>, IMapper<ChannelWebhookEntity, ChannelWebhook>
{
    public const int MaxNameLength   = 32;
    public const int MaxPerChannel   = 10;

    public          Guid            Id           { get; set; }
    public required Guid            SpaceId      { get; set; }
    public required Guid            ChannelId    { get; set; }
    public required string          Name         { get; set; }
    public          string?         AvatarFileId { get; set; }
    // SHA-256 of the token, lowercase hex.
    public required string          TokenHash    { get; set; }
    public required Guid            CreatorId    { get; set; }
    public          DateTimeOffset  CreatedAt    { get; set; }
    public          DateTimeOffset? LastUsedAt   { get; set; }

    public void Configure(EntityTypeBuilder<ChannelWebhookEntity> builder)
    {
        builder.ToTable("ChannelWebhooks");
        builder.HasKey(x => x.Id);

        builder.Property(x => x.Name).HasMaxLength(MaxNameLength).IsRequired();
        builder.Property(x => x.AvatarFileId).HasMaxLength(128);
        builder.Property(x => x.TokenHash).HasMaxLength(64).IsRequired();

        builder.HasIndex(x => x.ChannelId);
    }

    public static ChannelWebhook Map(scoped in ChannelWebhookEntity self)
        => new(self.Id, self.SpaceId, self.ChannelId, self.Name, self.AvatarFileId, self.CreatorId,
            self.CreatedAt.UtcDateTime, self.LastUsedAt?.UtcDateTime);

    /// <summary>A new token: 32 random bytes, base64url, safe in a URL path.</summary>
    public static string NewToken()
        => Convert.ToBase64String(RandomNumberGenerator.GetBytes(32)).Replace('+', '-').Replace('/', '_').TrimEnd('=');

    public static string HashToken(string token)
        => Convert.ToHexStringLower(SHA256.HashData(Encoding.UTF8.GetBytes(token)));

    public static bool TokenMatches(string token, string storedHash)
        => CryptographicOperations.FixedTimeEquals(
            Encoding.ASCII.GetBytes(HashToken(token)),
            Encoding.ASCII.GetBytes(storedHash));
}

/// <summary>The webhook a channel message came from, stored with the message.</summary>
[GenerateSerializer, Immutable]
public sealed record MessageWebhookAuthor(
    [property: Id(0)] Guid WebhookId,
    [property: Id(1)] string Name,
    [property: Id(2)] string? AvatarFileId);
