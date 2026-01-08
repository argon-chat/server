namespace Argon.Entities;

using Microsoft.EntityFrameworkCore.Metadata.Builders;

/// <summary>
/// Stores WebAuthn/Passkey credentials for a user.
/// </summary>
public record UserPasskeyEntity : ArgonEntity, IEntityTypeConfiguration<UserPasskeyEntity>
{
    public required Guid   UserId       { get; set; }
    public virtual  UserEntity User     { get; set; } = null!;
    
    [MaxLength(128)]
    public required string Name         { get; set; }
    
    [MaxLength(1024)]
    public string? PublicKey            { get; set; }
    
    [MaxLength(512)]
    public string? Challenge            { get; set; }
    
    public DateTimeOffset? LastUsedAt   { get; set; }
    
    /// <summary>
    /// Indicates if the passkey registration is complete.
    /// </summary>
    public bool IsCompleted             { get; set; }

    public void Configure(EntityTypeBuilder<UserPasskeyEntity> builder)
    {
        builder.HasKey(x => x.Id);
        
        builder.HasIndex(x => x.UserId);
        
        builder.HasOne(x => x.User)
           .WithMany()
           .HasForeignKey(x => x.UserId)
           .OnDelete(DeleteBehavior.Cascade);
    }
}
