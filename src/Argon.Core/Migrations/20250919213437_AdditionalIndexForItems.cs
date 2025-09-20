using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace Argon.Api.Migrations
{
    /// <inheritdoc />
    public partial class AdditionalIndexForItems : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.CreateIndex(
                name: "IX_Items_Id_IsReference",
                table: "Items",
                columns: new[] { "Id", "IsReference" });
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropIndex(
                name: "IX_Items_Id_IsReference",
                table: "Items");
        }
    }
}
