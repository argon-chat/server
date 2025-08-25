namespace Argon.Api.Entities.Configurations;

using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata.Builders;

public sealed class ArgonMessageCountersTypeConfiguration : IEntityTypeConfiguration<ArgonMessageCounters>
{
    public void Configure(EntityTypeBuilder<ArgonMessageCounters> builder)
    {
        builder.ToTable("ArgonMessages_Counters")
            .HasKey(x => new
            {
                x.ChannelId,
                x.ServerId
            });
    }
}
