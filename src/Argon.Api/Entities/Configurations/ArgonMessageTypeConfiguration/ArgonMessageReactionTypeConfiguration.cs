namespace Argon.Api.Entities.Configurations;

using Microsoft.EntityFrameworkCore.Metadata.Builders;

public sealed class ArgonMessageReactionTypeConfiguration : IEntityTypeConfiguration<ArgonMessageReaction>
{
    public void Configure(EntityTypeBuilder<ArgonMessageReaction> builder)
    {
        builder.HasKey(r => new
        {
            r.ServerId,
            r.ChannelId,
            r.MessageId,
            r.UserId,
            r.Reaction
        });

        builder.HasIndex(r => new
        {
            r.ServerId,
            r.ChannelId,
            r.MessageId
        })
        .IsUnique();
    }
}
