using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace Argon.Core.Migrations
{
    /// <inheritdoc />
    public partial class BotCommands : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.CreateTable(
                name: "BotCommands",
                columns: table => new
                {
                    CommandId = table.Column<Guid>(type: "uuid", nullable: false),
                    AppId = table.Column<Guid>(type: "uuid", nullable: false),
                    SpaceId = table.Column<Guid>(type: "uuid", nullable: true),
                    Name = table.Column<string>(type: "varchar(32)", maxLength: 32, nullable: false),
                    Description = table.Column<string>(type: "text", maxLength: 100, nullable: false),
                    Options = table.Column<string>(type: "jsonb", nullable: false),
                    DefaultPermission = table.Column<bool>(type: "boolean", nullable: false),
                    CreatedAt = table.Column<DateTime>(type: "timestamp with time zone", nullable: false),
                    UpdatedAt = table.Column<DateTime>(type: "timestamp with time zone", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_BotCommands", x => x.CommandId);
                    table.ForeignKey(
                        name: "FK_BotCommands_DevApps_AppId",
                        column: x => x.AppId,
                        principalTable: "DevApps",
                        principalColumn: "AppId",
                        onDelete: ReferentialAction.Cascade);
                });

            migrationBuilder.CreateIndex(
                name: "IX_BotCommands_AppId_SpaceId_Name",
                table: "BotCommands",
                columns: new[] { "AppId", "SpaceId", "Name" },
                unique: true);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropTable(
                name: "BotCommands");
        }
    }
}
