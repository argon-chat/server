using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace Argon.Core.Migrations
{
    /// <inheritdoc />
    public partial class ChannelFollowsAndWebhooks : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<string>(
                name: "Crosspost",
                table: "Messages",
                type: "jsonb",
                nullable: true);

            migrationBuilder.AddColumn<DateTimeOffset>(
                name: "PublishedAt",
                table: "Messages",
                type: "timestamp with time zone",
                nullable: true);

            migrationBuilder.AddColumn<string>(
                name: "Webhook",
                table: "Messages",
                type: "jsonb",
                nullable: true);

            migrationBuilder.CreateTable(
                name: "ChannelFollows",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "uuid", nullable: false),
                    SourceSpaceId = table.Column<Guid>(type: "uuid", nullable: false),
                    SourceChannelId = table.Column<Guid>(type: "uuid", nullable: false),
                    TargetSpaceId = table.Column<Guid>(type: "uuid", nullable: false),
                    TargetChannelId = table.Column<Guid>(type: "uuid", nullable: false),
                    CreatorId = table.Column<Guid>(type: "uuid", nullable: false),
                    CreatedAt = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_ChannelFollows", x => x.Id);
                });

            migrationBuilder.CreateTable(
                name: "ChannelWebhooks",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "uuid", nullable: false),
                    SpaceId = table.Column<Guid>(type: "uuid", nullable: false),
                    ChannelId = table.Column<Guid>(type: "uuid", nullable: false),
                    Name = table.Column<string>(type: "text", maxLength: 32, nullable: false),
                    AvatarFileId = table.Column<string>(type: "text", maxLength: 128, nullable: true),
                    TokenHash = table.Column<string>(type: "text", maxLength: 64, nullable: false),
                    CreatorId = table.Column<Guid>(type: "uuid", nullable: false),
                    CreatedAt = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false),
                    LastUsedAt = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_ChannelWebhooks", x => x.Id);
                });

            migrationBuilder.CreateIndex(
                name: "IX_Messages_SpaceId_ChannelId_PublishedAt",
                table: "Messages",
                columns: new[] { "SpaceId", "ChannelId", "PublishedAt" },
                filter: "\"PublishedAt\" IS NOT NULL");

            migrationBuilder.CreateIndex(
                name: "ix_channel_read_states_channel_mark",
                table: "ChannelReadStates",
                columns: new[] { "ChannelId", "LastReadMessageId" });

            migrationBuilder.CreateIndex(
                name: "IX_ChannelFollows_SourceChannelId_TargetChannelId",
                table: "ChannelFollows",
                columns: new[] { "SourceChannelId", "TargetChannelId" },
                unique: true);

            migrationBuilder.CreateIndex(
                name: "IX_ChannelFollows_TargetChannelId",
                table: "ChannelFollows",
                column: "TargetChannelId");

            migrationBuilder.CreateIndex(
                name: "IX_ChannelWebhooks_ChannelId",
                table: "ChannelWebhooks",
                column: "ChannelId");
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropTable(
                name: "ChannelFollows");

            migrationBuilder.DropTable(
                name: "ChannelWebhooks");

            migrationBuilder.DropIndex(
                name: "IX_Messages_SpaceId_ChannelId_PublishedAt",
                table: "Messages");

            migrationBuilder.DropIndex(
                name: "ix_channel_read_states_channel_mark",
                table: "ChannelReadStates");

            migrationBuilder.DropColumn(
                name: "Crosspost",
                table: "Messages");

            migrationBuilder.DropColumn(
                name: "PublishedAt",
                table: "Messages");

            migrationBuilder.DropColumn(
                name: "Webhook",
                table: "Messages");
        }
    }
}
