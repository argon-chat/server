namespace Argon.Core.Entities.Data;

using Microsoft.EntityFrameworkCore.Metadata.Builders;

public enum MuteTargetType
{
    Space,
    Channel
}

public enum MuteLevel
{
    None,
    OnlyMentions,
    All
}

public record MuteSettingsEntity : IEntityTypeConfiguration<MuteSettingsEntity>
{
    public required Guid           UserId           { get; set; }
    public required Guid           TargetId         { get; set; }
    public required MuteTargetType TargetType       { get; set; }
    public required MuteLevel      MuteLevel        { get; set; }
    public          DateTimeOffset? MuteExpiresAt   { get; set; }
    public          bool           SuppressEveryone { get; set; }
    public          DateTimeOffset  CreatedAt       { get; set; } = DateTimeOffset.UtcNow;

    public void Configure(EntityTypeBuilder<MuteSettingsEntity> builder)
    {
        builder.ToTable("MuteSettings");

        builder.HasKey(x => new { x.UserId, x.TargetId });

        builder.Property(x => x.UserId).IsRequired();
        builder.Property(x => x.TargetId).IsRequired();
        builder.Property(x => x.TargetType).IsRequired();
        builder.Property(x => x.MuteLevel).IsRequired();
        builder.Property(x => x.CreatedAt).IsRequired();

        builder.HasIndex(x => new { x.UserId, x.TargetType })
            .HasDatabaseName("ix_mute_settings_user_type");
    }
}
