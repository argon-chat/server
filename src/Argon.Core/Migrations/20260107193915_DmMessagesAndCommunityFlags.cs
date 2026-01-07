using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace Argon.Core.Migrations
{
    /// <inheritdoc />
    public partial class DmMessagesAndCommunityFlags : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<int>(
                name: "UnreadCount",
                table: "user_chats",
                type: "integer",
                nullable: false,
                defaultValue: 0);

            migrationBuilder.AddColumn<Guid>(
                name: "DefaultChannelId",
                table: "Spaces",
                type: "uuid",
                nullable: true);

            migrationBuilder.AddColumn<bool>(
                name: "IsCommunity",
                table: "Spaces",
                type: "boolean",
                nullable: false,
                defaultValue: false);

            migrationBuilder.CreateTable(
                name: "direct_messages",
                columns: table => new
                {
                    MessageId = table.Column<long>(type: "BIGINT", nullable: false, defaultValueSql: "unique_rowid()"),
                    SenderId = table.Column<Guid>(type: "uuid", nullable: false),
                    ReceiverId = table.Column<Guid>(type: "uuid", nullable: false),
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
                    table.PrimaryKey("PK_direct_messages", x => new { x.SenderId, x.ReceiverId, x.MessageId });
                });

            migrationBuilder.UpdateData(
                table: "Spaces",
                keyColumn: "Id",
                keyValue: new Guid("11111111-0000-1111-1111-111111111111"),
                columns: new[] { "DefaultChannelId", "IsCommunity" },
                values: new object[] { null, false });

            migrationBuilder.CreateIndex(
                name: "IX_direct_messages_CreatorId",
                table: "direct_messages",
                column: "CreatorId");

            migrationBuilder.CreateIndex(
                name: "ix_dm_conversation_time",
                table: "direct_messages",
                columns: new[] { "SenderId", "ReceiverId", "CreatedAt" })
                .Annotation("Npgsql:IndexInclude", new[] { "Text", "Entities" });
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropTable(
                name: "direct_messages");

            migrationBuilder.DropColumn(
                name: "UnreadCount",
                table: "user_chats");

            migrationBuilder.DropColumn(
                name: "DefaultChannelId",
                table: "Spaces");

            migrationBuilder.DropColumn(
                name: "IsCommunity",
                table: "Spaces");
        }
    }
}
