using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace Argon.Api.Migrations
{
    /// <inheritdoc />
    public partial class lockdownfeature : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<DateTime>(
                name: "LockDownExpiration",
                table: "Users",
                type: "timestamp with time zone",
                nullable: true);

            migrationBuilder.AddColumn<int>(
                name: "LockdownReason",
                table: "Users",
                type: "integer",
                nullable: false,
                defaultValue: 0);

            migrationBuilder.UpdateData(
                table: "Archetypes",
                keyColumn: "Id",
                keyValue: new Guid("11111111-3333-0000-1111-111111111111"),
                column: "CreatedAt",
                value: new DateTime(2024, 11, 23, 16, 1, 14, 205, DateTimeKind.Utc).AddTicks(8411));

            migrationBuilder.UpdateData(
                table: "Archetypes",
                keyColumn: "Id",
                keyValue: new Guid("11111111-4444-0000-1111-111111111111"),
                column: "CreatedAt",
                value: new DateTime(2024, 11, 23, 16, 1, 14, 205, DateTimeKind.Utc).AddTicks(8382));

            migrationBuilder.UpdateData(
                table: "Users",
                keyColumn: "Id",
                keyValue: new Guid("11111111-2222-1111-2222-111111111111"),
                columns: new[] { "LockDownExpiration", "LockdownReason" },
                values: new object[] { null, 0 });
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropColumn(
                name: "LockDownExpiration",
                table: "Users");

            migrationBuilder.DropColumn(
                name: "LockdownReason",
                table: "Users");

            migrationBuilder.UpdateData(
                table: "Archetypes",
                keyColumn: "Id",
                keyValue: new Guid("11111111-3333-0000-1111-111111111111"),
                column: "CreatedAt",
                value: new DateTime(2024, 11, 23, 16, 1, 14, 205, DateTimeKind.Utc).AddTicks(8382));

            migrationBuilder.UpdateData(
                table: "Archetypes",
                keyColumn: "Id",
                keyValue: new Guid("11111111-4444-0000-1111-111111111111"),
                column: "CreatedAt",
                value: new DateTime(2024, 11, 23, 16, 1, 14, 205, DateTimeKind.Utc).AddTicks(8411));
        }
    }
}
