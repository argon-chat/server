using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace Argon.Core.Migrations
{
    /// <inheritdoc />
    public partial class RecentChats : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.CreateTable(
                name: "user_chats",
                columns: table => new
                {
                    PeerId = table.Column<Guid>(type: "uuid", nullable: false),
                    UserId = table.Column<Guid>(type: "uuid", nullable: false),
                    IsPinned = table.Column<bool>(type: "boolean", nullable: false, defaultValue: false),
                    PinnedAt = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: true),
                    LastMessageAt = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false),
                    LastMessageText = table.Column<string>(type: "text", maxLength: 2048, nullable: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_user_chats", x => x.PeerId);
                });

            migrationBuilder.CreateIndex(
                name: "ix_user_chats_sort",
                table: "user_chats",
                columns: new[] { "UserId", "IsPinned", "PinnedAt", "LastMessageAt" },
                descending: new[] { false, true, true, true });

            migrationBuilder.CreateIndex(
                name: "ux_user_chats_unique",
                table: "user_chats",
                columns: new[] { "UserId", "PeerId" },
                unique: true);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropTable(
                name: "user_chats");
        }
    }
}
