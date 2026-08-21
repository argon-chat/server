namespace Argon.Entities;

using Microsoft.EntityFrameworkCore.Metadata.Builders;

public record OperatorEntity : ArgonEntity, IEntityTypeConfiguration<OperatorEntity>
{
    public required string  DisplayName            { get; set; }
    public required string  Email                  { get; set; }
    public          Guid?   UserId                  { get; set; }
    public virtual  UserEntity? User                { get; set; }
    public          bool    IsActive                { get; set; } = true;
    public          bool    IsSystemOperator        { get; set; }
    public          DateTimeOffset? LastAuthAt       { get; set; }

    public virtual ICollection<OperatorAppAccessEntity> AppAccess { get; set; } = new List<OperatorAppAccessEntity>();

    public virtual ICollection<OperatorCertificateEntity> Certificates { get; set; } = new List<OperatorCertificateEntity>();

    public void Configure(EntityTypeBuilder<OperatorEntity> builder)
    {
        builder.HasIndex(x => x.Email).IsUnique();
        builder.HasIndex(x => x.UserId).IsUnique();
        builder.Property(x => x.Email).HasMaxLength(256);
        builder.Property(x => x.DisplayName).HasMaxLength(256);

        builder.HasOne(x => x.User)
           .WithOne(x => x.Operator)
           .HasForeignKey<OperatorEntity>(x => x.UserId)
           .OnDelete(DeleteBehavior.SetNull);
    }
}
