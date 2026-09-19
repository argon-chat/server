using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace Argon.Core.Migrations
{
    /// <inheritdoc />
    public partial class DropLegacyProfilePresets : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropColumn(
                name: "BackgroundId",
                table: "UserProfiles");

            migrationBuilder.DropColumn(
                name: "VoiceCardEffectId",
                table: "UserProfiles");

            migrationBuilder.DropColumn(
                name: "AvatarFrameId",
                table: "UserProfiles");

            migrationBuilder.DropColumn(
                name: "NickEffectId",
                table: "UserProfiles");
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<int>(
                name: "BackgroundId",
                table: "UserProfiles",
                type: "integer",
                nullable: true);

            migrationBuilder.AddColumn<int>(
                name: "VoiceCardEffectId",
                table: "UserProfiles",
                type: "integer",
                nullable: true);

            migrationBuilder.AddColumn<int>(
                name: "AvatarFrameId",
                table: "UserProfiles",
                type: "integer",
                nullable: true);

            migrationBuilder.AddColumn<int>(
                name: "NickEffectId",
                table: "UserProfiles",
                type: "integer",
                nullable: true);
        }
    }
}
