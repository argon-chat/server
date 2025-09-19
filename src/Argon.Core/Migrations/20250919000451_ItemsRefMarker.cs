using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace Argon.Api.Migrations
{
    /// <inheritdoc />
    public partial class ItemsRefMarker : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropColumn(
                name: "ConcurrencyToken",
                table: "Items");

            migrationBuilder.AddColumn<bool>(
                name: "IsReference",
                table: "Items",
                type: "boolean",
                nullable: false,
                defaultValue: false);

            migrationBuilder.CreateIndex(
                name: "IX_Items_IsReference",
                table: "Items",
                column: "IsReference");
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropIndex(
                name: "IX_Items_IsReference",
                table: "Items");

            migrationBuilder.DropColumn(
                name: "IsReference",
                table: "Items");

            migrationBuilder.AddColumn<long>(
                name: "ConcurrencyToken",
                table: "Items",
                type: "bigint",
                nullable: false,
                defaultValue: 0L);
        }
    }
}
