namespace Argon.Entities;

using Microsoft.EntityFrameworkCore.Metadata.Builders;

public record FileCounterEntity : ArgonEntity, IEntityTypeConfiguration<FileCounterEntity>
{
    public long RefCount { get; set; } = 1;

    public void Configure(EntityTypeBuilder<FileCounterEntity> builder)
    {
        // Id == FileId (1:1 relation with FileEntity)
    }
}
