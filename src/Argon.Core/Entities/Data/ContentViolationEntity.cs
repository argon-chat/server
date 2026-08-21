namespace Argon.Entities;

using Argon.Features.Storage;
using Microsoft.EntityFrameworkCore.Metadata.Builders;

public record ContentViolationEntity : ArgonEntity, IEntityTypeConfiguration<ContentViolationEntity>
{
    public required Guid        UserId       { get; set; }
    public virtual  UserEntity  User         { get; set; } = null!;
    public          Guid        FileId       { get; set; }
    public          FilePurpose FilePurpose  { get; set; }
    public          int         StagesUsed   { get; set; }
    public          Dictionary<string, float> PrimaryScores  { get; set; } = new();
    public          Dictionary<string, float>? RefinedScores { get; set; }

    public void Configure(EntityTypeBuilder<ContentViolationEntity> builder)
    {
        builder.HasIndex(x => x.UserId);
        builder.HasIndex(x => x.CreatedAt);

        builder.HasOne(x => x.User)
           .WithMany()
           .HasForeignKey(x => x.UserId)
           .OnDelete(DeleteBehavior.Cascade);

        builder.Property(x => x.PrimaryScores)
           .HasColumnType("jsonb")
           .HasConversion(
                v => JsonConvert.SerializeObject(v),
                v => JsonConvert.DeserializeObject<Dictionary<string, float>>(v) ?? new());

        builder.Property(x => x.RefinedScores)
           .HasColumnType("jsonb")
           .HasConversion(
                v => v == null ? null : JsonConvert.SerializeObject(v),
                v => v == null ? null : JsonConvert.DeserializeObject<Dictionary<string, float>>(v));
    }
}
