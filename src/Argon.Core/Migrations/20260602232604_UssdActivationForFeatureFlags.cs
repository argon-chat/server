using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace Argon.Core.Migrations
{
    /// <inheritdoc />
    public partial class UssdActivationForFeatureFlags : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<string>(
                name: "UssdActivationCode",
                table: "FeatureFlags",
                type: "varchar(32)",
                maxLength: 32,
                nullable: true);

            migrationBuilder.CreateIndex(
                name: "IX_FeatureFlags_UssdActivationCode",
                table: "FeatureFlags",
                column: "UssdActivationCode",
                unique: true,
                filter: "\"UssdActivationCode\" IS NOT NULL");
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropIndex(
                name: "IX_FeatureFlags_UssdActivationCode",
                table: "FeatureFlags");

            migrationBuilder.DropColumn(
                name: "UssdActivationCode",
                table: "FeatureFlags");
        }
    }
}
