namespace Argon.Entities;

using Microsoft.EntityFrameworkCore.Metadata.Builders;

public record OperatorEntity : ArgonEntity, IEntityTypeConfiguration<OperatorEntity>
{
    public required string  DisplayName            { get; set; }
    public required string  Email                  { get; set; }
    public          Guid?   UserId                  { get; set; }
    public virtual  UserEntity? User                { get; set; }
    public          string? CertificateSerialNumber { get; set; }
    public          string? CertificateThumbprint   { get; set; }
    public          string? CertificateSubject      { get; set; }
    public          DateTimeOffset? CertificateNotBefore { get; set; }
    public          DateTimeOffset? CertificateNotAfter  { get; set; }
    public          bool    IsActive                { get; set; } = true;
    public          DateTimeOffset? LastAuthAt       { get; set; }

    public void Configure(EntityTypeBuilder<OperatorEntity> builder)
    {
        builder.HasIndex(x => x.Email).IsUnique();
        builder.HasIndex(x => x.UserId).IsUnique();
        builder.HasIndex(x => x.CertificateSerialNumber);
        builder.HasIndex(x => x.CertificateThumbprint);
        builder.Property(x => x.Email).HasMaxLength(256);
        builder.Property(x => x.DisplayName).HasMaxLength(256);
        builder.Property(x => x.CertificateSerialNumber).HasMaxLength(128);
        builder.Property(x => x.CertificateThumbprint).HasMaxLength(128);
        builder.Property(x => x.CertificateSubject).HasMaxLength(512);

        builder.HasOne(x => x.User)
           .WithOne(x => x.Operator)
           .HasForeignKey<OperatorEntity>(x => x.UserId)
           .OnDelete(DeleteBehavior.SetNull);
    }
}
