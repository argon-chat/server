namespace Argon.Entities;

using System.Globalization;
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
        builder.HasIndex(x => x.SpaceId);
    }

    public static ChannelWebhook Map(scoped in ChannelWebhookEntity self)
        => new(self.Id, self.SpaceId, self.ChannelId, self.Name, self.AvatarFileId, self.CreatorId,
            self.CreatedAt.UtcDateTime, self.LastUsedAt?.UtcDateTime);

    public const int TokenLength = 43;

    private static readonly string[] ReservedNames =
        ["argon", "system", "moderator", "admin", "administrator", "official", "support", "staff"];

    /// <summary>A new token: 32 random bytes, base64url, safe in a URL path.</summary>
    public static string NewToken()
        => Convert.ToBase64String(RandomNumberGenerator.GetBytes(32)).Replace('+', '-').Replace('/', '_').TrimEnd('=');

    /// <summary>Whether a string has the shape <see cref="NewToken"/> gives: 43 characters of base64url.</summary>
    public static bool IsTokenShaped(string? token)
        => token is { Length: TokenLength } && token.All(c => char.IsAsciiLetterOrDigit(c) || c is '-' or '_');

    /// <summary>The name without control, bidi or zero-width characters, trimmed.</summary>
    public static string CleanName(string? name)
    {
        if (string.IsNullOrEmpty(name))
            return "";

        var clean = new StringBuilder(name.Length);
        foreach (var rune in name.EnumerateRunes())
        {
            if (Rune.GetUnicodeCategory(rune) is UnicodeCategory.Control or UnicodeCategory.Format
                or UnicodeCategory.LineSeparator or UnicodeCategory.ParagraphSeparator)
                continue;
            clean.Append(rune.ToString());
        }

        return clean.ToString().Trim();
    }

    /// <summary>Names that pass for the platform or its staff, compared case-insensitively and with fullwidth letters folded.</summary>
    public static bool IsReservedName(string name)
    {
        // Fullwidth forms U+FF01..U+FF5E are ASCII shifted by 0xFEE0.
        var folded = string.Concat(name.Select(c => c is >= (char)0xFF01 and <= (char)0xFF5E ? (char)(c - 0xFEE0) : c)).Trim();
        return ReservedNames.Contains(folded, StringComparer.OrdinalIgnoreCase);
    }

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
