using System.Collections.Generic;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace Argon.Core.Migrations
{
    /// <inheritdoc />
    public partial class AppsRedirects : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<List<string>>(
                name: "AllowedRedirects",
                table: "DevApps",
                type: "text[]",
                nullable: false);

            migrationBuilder.AddColumn<bool>(
                name: "IsInternalApp",
                table: "DevApps",
                type: "boolean",
                nullable: false,
                defaultValue: false);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropColumn(
                name: "AllowedRedirects",
                table: "DevApps");

            migrationBuilder.DropColumn(
                name: "IsInternalApp",
                table: "DevApps");
        }
    }
}
