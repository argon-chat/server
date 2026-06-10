using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace Argon.Core.Migrations
{
    /// <inheritdoc />
    public partial class LegalVersions : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<string>(
                name: "AgreePrivacyVersion",
                table: "Users",
                type: "text",
                nullable: true);

            migrationBuilder.AddColumn<string>(
                name: "AgreeTosVersion",
                table: "Users",
                type: "text",
                nullable: true);

            migrationBuilder.UpdateData(
                table: "Users",
                keyColumn: "Id",
                keyValue: new Guid("11111111-2222-1111-2222-111111111111"),
                columns: new[] { "AgreePrivacyVersion", "AgreeTosVersion" },
                values: new object[] { null, null });

            migrationBuilder.UpdateData(
                table: "Users",
                keyColumn: "Id",
                keyValue: new Guid("44444444-2222-1111-2222-444444444444"),
                columns: new[] { "AgreePrivacyVersion", "AgreeTosVersion" },
                values: new object[] { null, null });
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropColumn(
                name: "AgreePrivacyVersion",
                table: "Users");

            migrationBuilder.DropColumn(
                name: "AgreeTosVersion",
                table: "Users");
        }
    }
}
