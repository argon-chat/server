using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace Argon.Core.Migrations
{
    /// <inheritdoc />
    public partial class SuperOperator : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<bool>(
                name: "IsSystemOperator",
                table: "Operators",
                type: "boolean",
                nullable: false,
                defaultValue: false);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropColumn(
                name: "IsSystemOperator",
                table: "Operators");
        }
    }
}
