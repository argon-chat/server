using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace Argon.Core.Migrations
{
    /// <inheritdoc />
    public partial class ClientAppEntity : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.CreateTable(
                name: "ClientApps",
                columns: table => new
                {
                    AppId = table.Column<Guid>(type: "uuid", nullable: false),
                    Platform = table.Column<int>(type: "integer", nullable: false),
                    RateLimitPerMinute = table.Column<int>(type: "integer", nullable: false),
                    IsVerified = table.Column<bool>(type: "boolean", nullable: false),
                    IsPublic = table.Column<bool>(type: "boolean", nullable: false),
                    WebsiteUrl = table.Column<string>(type: "text", nullable: true),
                    RepositoryUrl = table.Column<string>(type: "text", nullable: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_ClientApps", x => x.AppId);
                    table.ForeignKey(
                        name: "FK_ClientApps_DevApps_AppId",
                        column: x => x.AppId,
                        principalTable: "DevApps",
                        principalColumn: "AppId",
                        onDelete: ReferentialAction.Cascade);
                });
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropTable(
                name: "ClientApps");
        }
    }
}
