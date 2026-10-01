namespace Argon.Entities;

using Microsoft.EntityFrameworkCore.Metadata.Builders;

/// <summary>
/// One linked external account (a Discord "connection"). See
/// <c>docs/internal/architecture/connections.md</c>.
/// </summary>
/// <remarks>
/// <para>Not an <see cref="ArgonEntity"/>: a row is deleted outright on unlink, because both unique
/// indexes below have to be free again the moment a person disconnects and relinks, and a soft
/// deleted row would keep them taken.</para>
///
/// <para>Tokens are sealed by <c>TokenSealer</c> and opened only inside <c>UserConnectionsGrain</c>;
/// nothing else reads <see cref="SealedTokens"/>. Nothing here is copied from the users table.</para>
/// </remarks>
public record UserConnectionEntity : IEntityTypeConfiguration<UserConnectionEntity>
{
    public Guid Id     { get; set; }
    public Guid UserId { get; set; }

    public ConnectionProvider Provider { get; set; }

    /// <summary>The provider's stable id, never the handle.</summary>
    [MaxLength(128)] public required string ExternalId { get; set; }

    /// <summary>Handle, persona or channel title, as the provider shows it.</summary>
    [MaxLength(128)] public required string ExternalName { get; set; }

    [MaxLength(512)]  public string? ExternalUrl { get; set; }
    [MaxLength(1024)] public string? AvatarUrl   { get; set; }

    public bool DisplayOnProfile { get; set; } = true;
    public bool ShowDetails      { get; set; } = true;
    public bool DisplayAsStatus  { get; set; }
    public bool AllowListenAlong { get; set; } = true;

    /// <summary>What the provider actually granted; providers trim what was asked.</summary>
    [MaxLength(1024)] public string Scopes { get; set; } = "";

    public byte[]?         SealedTokens         { get; set; }
    public int             TokenKeyVersion      { get; set; }
    public DateTimeOffset? AccessTokenExpiresAt { get; set; }

    public List<ConnectionDetail> Details            { get; set; } = new();
    public DateTimeOffset?        DetailsRefreshedAt { get; set; }

    public ConnectionStatus Status { get; set; } = ConnectionStatus.ACTIVE;

    [MaxLength(512)] public string? LastError   { get; set; }
    public DateTimeOffset?          LastErrorAt { get; set; }

    public DateTimeOffset CreatedAt { get; set; }
    public DateTimeOffset UpdatedAt { get; set; }

    public void Configure(EntityTypeBuilder<UserConnectionEntity> builder)
    {
        builder.HasKey(x => x.Id);

        builder.Property(x => x.Details)
           .HasColumnType("jsonb")
           .HasConversion(
                v => JsonConvert.SerializeObject(v),
                v => JsonConvert.DeserializeObject<List<ConnectionDetail>>(v) ?? new List<ConnectionDetail>())
           .Metadata.SetValueComparer(new JsonValueComparer<List<ConnectionDetail>>());

        // One account per provider per user, and one Argon account per external identity.
        builder.HasIndex(x => new { x.UserId, x.Provider }).IsUnique();
        builder.HasIndex(x => new { x.Provider, x.ExternalId }).IsUnique();

        // The maintenance grain's two scans.
        builder.HasIndex(x => x.AccessTokenExpiresAt);
        builder.HasIndex(x => x.DetailsRefreshedAt);

        builder.HasOne<UserEntity>()
           .WithMany()
           .HasForeignKey(x => x.UserId)
           .OnDelete(DeleteBehavior.Cascade);
    }
}
