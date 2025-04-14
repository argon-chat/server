using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace Argon.Api.Migrations
{
    /// <inheritdoc />
    public partial class MessagesCounters : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropIndex(
                name: "IX_ArgonMessageReactions_ServerId_ChannelId_MessageId",
                table: "ArgonMessageReactions");

            migrationBuilder.CreateTable(
                name: "ArgonMessages_Counters",
                columns: table => new
                {
                    ServerId = table.Column<Guid>(type: "uuid", nullable: false),
                    ChannelId = table.Column<Guid>(type: "uuid", nullable: false),
                    NextMessageId = table.Column<decimal>(type: "numeric(20,0)", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_ArgonMessages_Counters", x => new { x.ChannelId, x.ServerId });
                });

            migrationBuilder.CreateIndex(
                name: "IX_ArgonMessageReactions_ServerId_ChannelId_MessageId",
                table: "ArgonMessageReactions",
                columns: new[] { "ServerId", "ChannelId", "MessageId" },
                unique: true);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropTable(
                name: "ArgonMessages_Counters");

            migrationBuilder.DropIndex(
                name: "IX_ArgonMessageReactions_ServerId_ChannelId_MessageId",
                table: "ArgonMessageReactions");

            migrationBuilder.CreateIndex(
                name: "IX_ArgonMessageReactions_ServerId_ChannelId_MessageId",
                table: "ArgonMessageReactions",
                columns: new[] { "ServerId", "ChannelId", "MessageId" });
        }
    }
}
