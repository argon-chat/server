namespace Argon.Entities;

using Microsoft.EntityFrameworkCore.Metadata.Builders;

/// <summary>
/// A certificate issued to an operator. An operator may have multiple active
/// certificates at once (one per device/YubiKey). A certificate is considered
/// active while <see cref="RevokedAt"/> is null.
/// </summary>
public record OperatorCertificateEntity : ArgonEntity, IEntityTypeConfiguration<OperatorCertificateEntity>
{
    public required Guid   OperatorId   { get; set; }
    public virtual  OperatorEntity Operator { get; set; } = null!;

    public required string SerialNumber { get; set; }
    public required string Thumbprint   { get; set; }
    public required string Subject      { get; set; }
    public          DateTimeOffset NotBefore { get; set; }
    public          DateTimeOffset NotAfter  { get; set; }

    /// <summary>Human-readable name of the device/reader the cert was enrolled from (e.g. "YubiKey 5 NFC").</summary>
    public string? DeviceName         { get; set; }
    /// <summary>Serial number of the device/reader the cert was enrolled from.</summary>
    public string? DeviceSerialNumber { get; set; }

    /// <summary>When the certificate was revoked. Null means the certificate is active.</summary>
    public DateTimeOffset? RevokedAt { get; set; }

    public void Configure(EntityTypeBuilder<OperatorCertificateEntity> builder)
    {
        builder.ToTable("OperatorCertificates");

        builder.HasIndex(x => x.Thumbprint);
        builder.HasIndex(x => x.SerialNumber);
        builder.HasIndex(x => x.OperatorId);

        builder.Property(x => x.SerialNumber).HasMaxLength(128);
        builder.Property(x => x.Thumbprint).HasMaxLength(128);
        builder.Property(x => x.Subject).HasMaxLength(512);
        builder.Property(x => x.DeviceName).HasMaxLength(256);
        builder.Property(x => x.DeviceSerialNumber).HasMaxLength(128);

        builder.HasOne(x => x.Operator)
           .WithMany(x => x.Certificates)
           .HasForeignKey(x => x.OperatorId)
           .OnDelete(DeleteBehavior.Cascade);
    }
}
