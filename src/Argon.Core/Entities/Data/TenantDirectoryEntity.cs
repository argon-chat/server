namespace Argon.Entities;

using Microsoft.EntityFrameworkCore.Metadata.Builders;

/// <summary>
/// Maps an email domain (e.g. "acme.example") to a managed/self-hosted Argon instance URL.
/// Consumed anonymously by the desktop client via /api/discovery/resolve to route enterprise
/// sign-ins to the right instance. Only <see cref="IsVerified"/> rows are served.
/// </summary>
public record TenantDirectoryEntity : ArgonEntity, IEntityTypeConfiguration<TenantDirectoryEntity>
{
    /// <summary>Normalized, lower-case email domain. Unique. e.g. "acme.example".</summary>
    public required string  Domain      { get; set; }
    /// <summary>https base URL of the managed instance; the client fetches its manifest from here.</summary>
    public required string  InstanceUrl { get; set; }
    /// <summary>Only verified rows are returned by resolve (prevents domain hijacking).</summary>
    public          bool    IsVerified  { get; set; }
    public          string? OrgName     { get; set; }
    public          Guid?   OwnerUserId { get; set; }
    public          string? Notes       { get; set; }

    public void Configure(EntityTypeBuilder<TenantDirectoryEntity> builder)
    {
        builder.HasIndex(x => x.Domain).IsUnique();
        builder.Property(x => x.Domain).HasMaxLength(253);      // max DNS name length
        builder.Property(x => x.InstanceUrl).HasMaxLength(512);
        builder.Property(x => x.OrgName).HasMaxLength(256);
        builder.Property(x => x.Notes).HasMaxLength(1024);
    }
}
