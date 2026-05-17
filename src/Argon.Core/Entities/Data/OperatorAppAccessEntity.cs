namespace Argon.Entities;

using Microsoft.EntityFrameworkCore.Metadata.Builders;

/// <summary>
/// Maps an operator to a specific application with scoped permissions.
/// When at least one record exists for an operator, they can ONLY access
/// apps with an explicit active access record (explicit-allow model).
/// When NO records exist for an operator, the permissive fallback applies
/// and the operator can access all internal apps (backward compat / self-hoster friendly).
/// </summary>
public class OperatorAppAccessEntity : IEntityTypeConfiguration<OperatorAppAccessEntity>
{
    public Guid OperatorId { get; set; }
    public Guid AppId      { get; set; }

    public virtual OperatorEntity                    Operator { get; set; } = null!;
    public virtual Argon.Core.Entities.Data.DevAppEntity App  { get; set; } = null!;

    /// <summary>
    /// OAuth scopes the operator is allowed to use for this app.
    /// Empty list = all of the app's RequiredScopes are granted (convenience default).
    /// </summary>
    public List<string> AllowedScopes { get; set; } = new();

    /// <summary>
    /// Additional app-specific claims injected into the operator's access token.
    /// Each app can define its own claim vocabulary (e.g. "finance:read", "finance:write").
    /// </summary>
    public List<string> Claims { get; set; } = new();

    public Guid            GrantedBy { get; set; }
    public DateTimeOffset  GrantedAt { get; set; } = DateTimeOffset.UtcNow;
    public bool            IsActive  { get; set; } = true;

    public void Configure(EntityTypeBuilder<OperatorAppAccessEntity> builder)
    {
        builder.ToTable("OperatorAppAccess");

        builder.HasKey(x => new { x.OperatorId, x.AppId });

        builder.HasOne(x => x.Operator)
           .WithMany(x => x.AppAccess)
           .HasForeignKey(x => x.OperatorId)
           .OnDelete(DeleteBehavior.Cascade);

        builder.HasOne(x => x.App)
           .WithMany()
           .HasForeignKey(x => x.AppId)
           .OnDelete(DeleteBehavior.Cascade);

        builder.HasIndex(x => x.OperatorId);
        builder.HasIndex(x => x.GrantedBy);

        builder
           .Property(x => x.AllowedScopes)
           .HasColumnType("text[]");

        builder
           .Property(x => x.Claims)
           .HasColumnType("text[]");
    }
}
