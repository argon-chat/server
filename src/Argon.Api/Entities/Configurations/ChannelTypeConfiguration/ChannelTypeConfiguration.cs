namespace Argon.Api.Entities.Configurations;

using Microsoft.EntityFrameworkCore.Metadata.Builders;

public sealed class ChannelTypeConfiguration : IEntityTypeConfiguration<Channel>
{
    public void Configure(EntityTypeBuilder<Channel> builder)
    {
        builder.HasOne(c => c.Server)
           .WithMany(s => s.Channels)
           .HasForeignKey(c => c.ServerId);

        builder.HasIndex(x => new
        {
            x.Id,
            x.ServerId
        });
    }
}
