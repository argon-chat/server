namespace Argon.Entities;

using Microsoft.EntityFrameworkCore.Metadata.Builders;

/// <summary>
/// Stores auto-delete settings for a user account.
/// </summary>
public record UserAutoDeleteSettingEntity : ArgonEntity, IEntityTypeConfiguration<UserAutoDeleteSettingEntity>
{
    public required Guid UserId      { get; set; }
    public virtual  UserEntity User  { get; set; } = null!;
    
    /// <summary>
    /// Number of months of inactivity before auto-deletion. Null means disabled.
    /// </summary>
    public int? Months               { get; set; }
    
    /// <summary>
    /// Whether auto-deletion is enabled.
    /// </summary>
    public bool Enabled              { get; set; }

    public void Configure(EntityTypeBuilder<UserAutoDeleteSettingEntity> builder)
    {
        builder.HasKey(x => x.Id);
        
        builder.HasIndex(x => x.UserId).IsUnique();
        
        builder.HasOne(x => x.User)
           .WithMany()
           .HasForeignKey(x => x.UserId)
           .OnDelete(DeleteBehavior.Cascade);
    }
}
