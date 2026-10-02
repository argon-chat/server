using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace Argon.Core.Migrations
{
    /// <inheritdoc />
    public partial class DevAppTexts : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<int>(
                name: "TextsVersion",
                table: "DevApps",
                type: "integer",
                nullable: false,
                defaultValue: 0);

            migrationBuilder.CreateTable(
                name: "DevAppTexts",
                columns: table => new
                {
                    AppId = table.Column<Guid>(type: "uuid", nullable: false),
                    Key = table.Column<string>(type: "text", maxLength: 64, nullable: false),
                    Locale = table.Column<string>(type: "text", maxLength: 16, nullable: false),
                    Value = table.Column<string>(type: "text", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_DevAppTexts", x => new { x.AppId, x.Key, x.Locale });
                    table.ForeignKey(
                        name: "FK_DevAppTexts_DevApps_AppId",
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
                name: "DevAppTexts");

            migrationBuilder.DropColumn(
                name: "TextsVersion",
                table: "DevApps");
        }
    }
}
