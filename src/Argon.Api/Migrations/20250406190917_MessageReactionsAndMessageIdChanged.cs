using System;
using Microsoft.EntityFrameworkCore.Migrations;
using Npgsql.EntityFrameworkCore.PostgreSQL.Metadata;

#nullable disable

namespace Argon.Api.Migrations
{
    /// <inheritdoc />
    public partial class MessageReactionsAndMessageIdChanged : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.CreateTable(
                name: "ArgonMessageReactions",
                columns: table => new
                {
                    ServerId = table.Column<Guid>(type: "uuid", nullable: false),
                    ChannelId = table.Column<Guid>(type: "uuid", nullable: false),
                    MessageId = table.Column<decimal>(type: "numeric(20,0)", nullable: false),
                    UserId = table.Column<Guid>(type: "uuid", nullable: false),
                    Reaction = table.Column<string>(type: "character varying(32)", maxLength: 32, nullable: false),
                    Timestamp = table.Column<DateTime>(type: "timestamp with time zone", nullable: false),
                    Id = table.Column<Guid>(type: "uuid", nullable: false),
                    CreatedAt = table.Column<DateTime>(type: "timestamp with time zone", nullable: false),
                    UpdatedAt = table.Column<DateTime>(type: "timestamp with time zone", nullable: false),
                    DeletedAt = table.Column<DateTime>(type: "timestamp with time zone", nullable: true),
                    IsDeleted = table.Column<bool>(type: "boolean", nullable: false),
                    CreatorId = table.Column<Guid>(type: "uuid", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_ArgonMessageReactions", x => new { x.ServerId, x.ChannelId, x.MessageId, x.UserId, x.Reaction });
                });

            migrationBuilder.CreateTable(
                name: "MeetInviteLinks",
                columns: table => new
                {
                    Id = table.Column<decimal>(type: "numeric(20,0)", nullable: false),
                    AssociatedChannelId = table.Column<Guid>(type: "uuid", nullable: true),
                    AssociatedServerId = table.Column<Guid>(type: "uuid", nullable: true),
                    NoChannelSharedKey = table.Column<Guid>(type: "uuid", nullable: true),
                    ExpireDate = table.Column<DateTime>(type: "timestamp with time zone", nullable: false),
                    CreatedAt = table.Column<DateTime>(type: "timestamp with time zone", nullable: false),
                    UpdatedAt = table.Column<DateTime>(type: "timestamp with time zone", nullable: false),
                    DeletedAt = table.Column<DateTime>(type: "timestamp with time zone", nullable: true),
                    IsDeleted = table.Column<bool>(type: "boolean", nullable: false),
                    CreatorId = table.Column<Guid>(type: "uuid", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_MeetInviteLinks", x => x.Id);
                });

            migrationBuilder.CreateIndex(
                name: "IX_ArgonMessageReactions_CreatorId",
                table: "ArgonMessageReactions",
                column: "CreatorId");

            migrationBuilder.CreateIndex(
                name: "IX_ArgonMessageReactions_ServerId_ChannelId_MessageId",
                table: "ArgonMessageReactions",
                columns: new[] { "ServerId", "ChannelId", "MessageId" });

            migrationBuilder.CreateIndex(
                name: "IX_MeetInviteLinks_CreatorId",
                table: "MeetInviteLinks",
                column: "CreatorId");
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropTable(
                name: "ArgonMessageReactions");

            migrationBuilder.DropTable(
                name: "MeetInviteLinks");
        }
    }
}
