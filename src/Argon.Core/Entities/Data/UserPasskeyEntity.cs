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
    
    /// <summary>
    /// The WebAuthn credential ID returned by the authenticator during registration (base64url).
    /// </summary>
    [MaxLength(1024)]
    public byte[]? CredentialId         { get; set; }
    
    /// <summary>
    /// The COSE-format public key extracted from the attestation during registration.
    /// </summary>
    [MaxLength(2048)]
    public byte[]? PublicKey            { get; set; }
    
    /// <summary>
    /// WebAuthn signature counter for detecting cloned authenticators.
    /// </summary>
    public uint SignCount               { get; set; }
    
    /// <summary>
    /// Authenticator AAGUID — identifies the make/model of the authenticator (e.g. YubiKey 5, Windows Hello).
    /// </summary>
    public Guid? AaGuid                 { get; set; }
    
    public DateTimeOffset? LastUsedAt   { get; set; }
    
    /// <summary>
    /// Indicates if the passkey registration is complete.
    /// </summary>
    public bool IsCompleted             { get; set; }

    public void Configure(EntityTypeBuilder<UserPasskeyEntity> builder)
    {
        builder.HasKey(x => x.Id);
        
        builder.HasIndex(x => x.UserId);
        builder.HasIndex(x => x.CredentialId);
        
        builder.HasOne(x => x.User)
           .WithMany()
           .HasForeignKey(x => x.UserId)
           .OnDelete(DeleteBehavior.Cascade);
    }
}
