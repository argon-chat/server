using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace Argon.Core.Migrations
{
    /// <inheritdoc />
    public partial class ReworkConversations : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.CreateTable(
                name: "conversations",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "uuid", nullable: false),
                    Participant1Id = table.Column<Guid>(type: "uuid", nullable: false),
                    Participant2Id = table.Column<Guid>(type: "uuid", nullable: false),
                    CreatedAt = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false),
                    LastMessageAt = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: true),
                    LastMessageText = table.Column<string>(type: "text", maxLength: 2048, nullable: true),
                    LastMessageSenderId = table.Column<Guid>(type: "uuid", nullable: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_conversations", x => x.Id);
                });

            migrationBuilder.CreateTable(
                name: "direct_messages_v2",
                columns: table => new
                {
                    MessageId = table.Column<long>(type: "BIGINT", nullable: false, defaultValueSql: "unique_rowid()"),
                    ConversationId = table.Column<Guid>(type: "uuid", nullable: false),
                    SenderId = table.Column<Guid>(type: "uuid", nullable: false),
                    ReplyTo = table.Column<long>(type: "BIGINT", nullable: true),
                    Text = table.Column<string>(type: "text", maxLength: 4096, nullable: false),
                    Entities = table.Column<string>(type: "jsonb", nullable: false),
                    CreatedAt = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false),
                    UpdatedAt = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false),
                    DeletedAt = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: true),
                    IsDeleted = table.Column<bool>(type: "boolean", nullable: false),
                    CreatorId = table.Column<Guid>(type: "uuid", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_direct_messages_v2", x => new { x.ConversationId, x.MessageId });
                });

            migrationBuilder.CreateTable(
                name: "user_conversations",
                columns: table => new
                {
                    UserId = table.Column<Guid>(type: "uuid", nullable: false),
                    ConversationId = table.Column<Guid>(type: "uuid", nullable: false),
                    PeerId = table.Column<Guid>(type: "uuid", nullable: false),
                    IsPinned = table.Column<bool>(type: "boolean", nullable: false, defaultValue: false),
                    PinnedAt = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: true),
                    UnreadCount = table.Column<int>(type: "integer", nullable: false, defaultValue: 0),
                    LastReadMessageId = table.Column<long>(type: "bigint", nullable: true),
                    LastMessageAt = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false),
                    LastMessageText = table.Column<string>(type: "text", maxLength: 2048, nullable: true),
                    IsArchived = table.Column<bool>(type: "boolean", nullable: false, defaultValue: false),
                    IsMuted = table.Column<bool>(type: "boolean", nullable: false, defaultValue: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_user_conversations", x => new { x.UserId, x.ConversationId });
                });

            migrationBuilder.CreateIndex(
                name: "ix_conversations_participant1",
                table: "conversations",
                column: "Participant1Id");

            migrationBuilder.CreateIndex(
                name: "ix_conversations_participant2",
                table: "conversations",
                column: "Participant2Id");

            migrationBuilder.CreateIndex(
                name: "ix_conversations_participants",
                table: "conversations",
                columns: new[] { "Participant1Id", "Participant2Id" },
                unique: true);

            migrationBuilder.CreateIndex(
                name: "IX_direct_messages_v2_CreatorId",
                table: "direct_messages_v2",
                column: "CreatorId");

            migrationBuilder.CreateIndex(
                name: "ix_dm_v2_conversation_time",
                table: "direct_messages_v2",
                columns: new[] { "ConversationId", "CreatedAt" },
                descending: new[] { false, true });

            migrationBuilder.CreateIndex(
                name: "ix_dm_v2_sender",
                table: "direct_messages_v2",
                column: "SenderId");

            migrationBuilder.CreateIndex(
                name: "ix_user_conversations_conversation",
                table: "user_conversations",
                column: "ConversationId");

            migrationBuilder.CreateIndex(
                name: "ix_user_conversations_peer",
                table: "user_conversations",
                columns: new[] { "UserId", "PeerId" },
                unique: true);

            migrationBuilder.CreateIndex(
                name: "ix_user_conversations_sort",
                table: "user_conversations",
                columns: new[] { "UserId", "IsArchived", "IsPinned", "PinnedAt", "LastMessageAt" },
                descending: new[] { false, false, true, true, true });
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropTable(
                name: "conversations");

            migrationBuilder.DropTable(
                name: "direct_messages_v2");

            migrationBuilder.DropTable(
                name: "user_conversations");
        }
    }
}
