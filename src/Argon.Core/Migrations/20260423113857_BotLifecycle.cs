using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace Argon.Core.Migrations
{
    /// <inheritdoc />
    public partial class BotLifecycle : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropColumn(
                name: "IsPublic",
                table: "Bots");

            migrationBuilder.DropColumn(
                name: "IsRestricted",
                table: "Bots");

            migrationBuilder.AddColumn<int>(
                name: "LifecycleState",
                table: "Bots",
                type: "integer",
                nullable: false,
                defaultValue: 0);

            migrationBuilder.AddColumn<decimal>(
                name: "RequiredEntitlements",
                table: "Bots",
                type: "numeric(20,0)",
                nullable: false,
                defaultValue: 3m);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropColumn(
                name: "LifecycleState",
                table: "Bots");

            migrationBuilder.DropColumn(
                name: "RequiredEntitlements",
                table: "Bots");

            migrationBuilder.AddColumn<bool>(
                name: "IsPublic",
                table: "Bots",
                type: "boolean",
                nullable: false,
                defaultValue: false);

            migrationBuilder.AddColumn<bool>(
                name: "IsRestricted",
                table: "Bots",
                type: "boolean",
                nullable: false,
                defaultValue: false);
        }
    }
}
