using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace Argon.Core.Migrations
{
    /// <inheritdoc />
    public partial class BotsCoveringIndex : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropIndex(
                name: "IX_Bots_BotAsUserId",
                table: "Bots");

            migrationBuilder.CreateIndex(
                name: "IX_Bots_BotAsUserId",
                table: "Bots",
                column: "BotAsUserId",
                unique: true)
                .Annotation("Npgsql:CreatedConcurrently", true)
                .Annotation("Npgsql:IndexInclude", new[] { "BotToken", "RequiresOAuth2", "AllowDMs", "IsVerified", "MaxSpaces", "LifecycleState", "RequiredEntitlements", "EntitlementsVersion" });
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropIndex(
                name: "IX_Bots_BotAsUserId",
                table: "Bots");

            migrationBuilder.CreateIndex(
                name: "IX_Bots_BotAsUserId",
                table: "Bots",
                column: "BotAsUserId",
                unique: true);
        }
    }
}
