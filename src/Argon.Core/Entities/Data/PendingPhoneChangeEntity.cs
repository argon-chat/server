namespace Argon.Entities;

using Microsoft.EntityFrameworkCore.Metadata.Builders;

/// <summary>
/// Stores pending phone change requests until verification is complete.
/// </summary>
public record PendingPhoneChangeEntity : ArgonEntity, IEntityTypeConfiguration<PendingPhoneChangeEntity>
{
    public required Guid UserId        { get; set; }
    public virtual  UserEntity User    { get; set; } = null!;
    
    [MaxLength(32)]
    public required string NewPhone    { get; set; }
    
    [MaxLength(512)]
    public required string CodeHash    { get; set; }
    
    [MaxLength(128)]
    public required string CodeSalt    { get; set; }
    
    public DateTimeOffset ExpiresAt    { get; set; }
    
    public int AttemptsLeft            { get; set; } = 5;

    public void Configure(EntityTypeBuilder<PendingPhoneChangeEntity> builder)
    {
        builder.HasKey(x => x.Id);
        
        builder.HasIndex(x => x.UserId);
        builder.HasIndex(x => x.ExpiresAt);
        
        builder.HasOne(x => x.User)
           .WithMany()
           .HasForeignKey(x => x.UserId)
           .OnDelete(DeleteBehavior.Cascade);
    }
}
