using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace Argon.Core.Migrations
{
    /// <inheritdoc />
    public partial class ArchetypeOrder : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<int>(
                name: "Order",
                table: "Archetypes",
                type: "integer",
                nullable: true);

            migrationBuilder.UpdateData(
                table: "Archetypes",
                keyColumn: "Id",
                keyValue: new Guid("11111111-3333-0000-1111-111111111111"),
                column: "Order",
                value: null);

            migrationBuilder.UpdateData(
                table: "Archetypes",
                keyColumn: "Id",
                keyValue: new Guid("11111111-4444-0000-1111-111111111111"),
                column: "Order",
                value: null);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropColumn(
                name: "Order",
                table: "Archetypes");
        }
    }
}
