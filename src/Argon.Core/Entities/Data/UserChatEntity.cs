namespace Argon.Core.Entities.Data;

using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata.Builders;

public class UserChatEntity : IMapper<UserChatEntity, UserChat>, IEntityTypeConfiguration<UserChatEntity>
{
    public const string TableName = "user_chats";

    public Guid UserId { get; set; }
    public Guid PeerId { get; set; }

    public bool            IsPinned { get; set; }
    public DateTimeOffset? PinnedAt { get; set; }

    public DateTimeOffset LastMessageAt { get; set; }

    [MaxLength(2048)]
    public string? LastMessageText { get; set; }

    public int UnreadCount { get; set; }

    public void Configure(EntityTypeBuilder<UserChatEntity> b)
    {
        b.ToTable(TableName);
        b.HasKey(x => new
        {
            x.UserId,
            x.PeerId
        });

        b.Property(x => x.UserId).IsRequired();
        b.Property(x => x.PeerId).IsRequired();

        b.Property(x => x.IsPinned)
           .HasDefaultValue(false);

        b.Property(x => x.LastMessageAt)
           .IsRequired();

        b.Property(x => x.LastMessageText)
           .HasMaxLength(2048);

        b.Property(x => x.UnreadCount)
           .HasDefaultValue(0);

        b.HasIndex(x => new
            {
                x.UserId,
                x.IsPinned,
                x.PinnedAt,
                x.LastMessageAt
            })
           .HasDatabaseName("ix_user_chats_sort")
           .IsDescending(false, true, true, true);
    }

    public static UserChat Map(scoped in UserChatEntity self)
        => new(self.PeerId, self.IsPinned, self.UserId, self.LastMessageText, self.LastMessageAt.UtcDateTime, self.PinnedAt?.UtcDateTime, self.UnreadCount);
}