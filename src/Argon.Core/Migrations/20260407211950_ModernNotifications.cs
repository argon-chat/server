using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace Argon.Core.Migrations
{
    /// <inheritdoc />
    public partial class ModernNotifications : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<long>(
                name: "LastMessageId",
                table: "conversations",
                type: "bigint",
                nullable: false,
                defaultValue: 0L);

            migrationBuilder.AddColumn<long>(
                name: "LastMessageId",
                table: "Channels",
                type: "bigint",
                nullable: false,
                defaultValue: 0L);

            migrationBuilder.CreateTable(
                name: "ChannelReadStates",
                columns: table => new
                {
                    UserId = table.Column<Guid>(type: "uuid", nullable: false),
                    ChannelId = table.Column<Guid>(type: "uuid", nullable: false),
                    SpaceId = table.Column<Guid>(type: "uuid", nullable: true),
                    LastReadMessageId = table.Column<long>(type: "bigint", nullable: false),
                    MentionCount = table.Column<int>(type: "integer", nullable: false),
                    UpdatedAt = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_ChannelReadStates", x => new { x.UserId, x.ChannelId });
                });

            migrationBuilder.CreateTable(
                name: "MuteSettings",
                columns: table => new
                {
                    UserId = table.Column<Guid>(type: "uuid", nullable: false),
                    TargetId = table.Column<Guid>(type: "uuid", nullable: false),
                    TargetType = table.Column<int>(type: "integer", nullable: false),
                    MuteLevel = table.Column<int>(type: "integer", nullable: false),
                    MuteExpiresAt = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: true),
                    SuppressEveryone = table.Column<bool>(type: "boolean", nullable: false),
                    CreatedAt = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_MuteSettings", x => new { x.UserId, x.TargetId });
                });

            migrationBuilder.CreateTable(
                name: "SystemNotifications",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "uuid", nullable: false),
                    UserId = table.Column<Guid>(type: "uuid", nullable: false),
                    Type = table.Column<string>(type: "text", maxLength: 64, nullable: false),
                    ReferenceId = table.Column<Guid>(type: "uuid", nullable: true),
                    Title = table.Column<string>(type: "text", maxLength: 512, nullable: false),
                    Body = table.Column<string>(type: "text", maxLength: 2048, nullable: true),
                    IsRead = table.Column<bool>(type: "boolean", nullable: false),
                    CreatedAt = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false),
                    ExpiresAt = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_SystemNotifications", x => x.Id);
                });

            migrationBuilder.CreateIndex(
                name: "ix_channel_read_states_user",
                table: "ChannelReadStates",
                column: "UserId");

            migrationBuilder.CreateIndex(
                name: "ix_channel_read_states_user_space",
                table: "ChannelReadStates",
                columns: new[] { "UserId", "SpaceId" });

            migrationBuilder.CreateIndex(
                name: "ix_mute_settings_user_type",
                table: "MuteSettings",
                columns: new[] { "UserId", "TargetType" });

            migrationBuilder.CreateIndex(
                name: "ix_system_notifications_user_feed",
                table: "SystemNotifications",
                columns: new[] { "UserId", "CreatedAt" },
                descending: new[] { false, true });

            migrationBuilder.CreateIndex(
                name: "ix_system_notifications_user_unread",
                table: "SystemNotifications",
                columns: new[] { "UserId", "IsRead", "CreatedAt" },
                descending: new[] { false, false, true });
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropTable(
                name: "ChannelReadStates");

            migrationBuilder.DropTable(
                name: "MuteSettings");

            migrationBuilder.DropTable(
                name: "SystemNotifications");

            migrationBuilder.DropColumn(
                name: "LastMessageId",
                table: "conversations");

            migrationBuilder.DropColumn(
                name: "LastMessageId",
                table: "Channels");
        }
    }
}
