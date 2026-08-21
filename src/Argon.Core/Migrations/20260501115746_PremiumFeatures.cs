using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace Argon.Core.Migrations
{
    /// <inheritdoc />
    public partial class PremiumFeatures : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropColumn(
                name: "BannerFileId",
                table: "UserProfiles");

            migrationBuilder.AddColumn<DateTimeOffset>(
                name: "DisplayNameChangedAt",
                table: "Users",
                type: "timestamp with time zone",
                nullable: true);

            migrationBuilder.AddColumn<int>(
                name: "AccentColor",
                table: "UserProfiles",
                type: "integer",
                nullable: true);

            migrationBuilder.AddColumn<int>(
                name: "AvatarFrameId",
                table: "UserProfiles",
                type: "integer",
                nullable: true);

            migrationBuilder.AddColumn<int>(
                name: "BackgroundId",
                table: "UserProfiles",
                type: "integer",
                nullable: true);

            migrationBuilder.AddColumn<int>(
                name: "NickEffectId",
                table: "UserProfiles",
                type: "integer",
                nullable: true);

            migrationBuilder.AddColumn<int>(
                name: "PrimaryColor",
                table: "UserProfiles",
                type: "integer",
                nullable: true);

            migrationBuilder.AddColumn<int>(
                name: "VoiceCardEffectId",
                table: "UserProfiles",
                type: "integer",
                nullable: true);

            migrationBuilder.UpdateData(
                table: "Users",
                keyColumn: "Id",
                keyValue: new Guid("11111111-2222-1111-2222-111111111111"),
                column: "DisplayNameChangedAt",
                value: null);

            migrationBuilder.UpdateData(
                table: "Users",
                keyColumn: "Id",
                keyValue: new Guid("44444444-2222-1111-2222-444444444444"),
                column: "DisplayNameChangedAt",
                value: null);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropColumn(
                name: "DisplayNameChangedAt",
                table: "Users");

            migrationBuilder.DropColumn(
                name: "AccentColor",
                table: "UserProfiles");

            migrationBuilder.DropColumn(
                name: "AvatarFrameId",
                table: "UserProfiles");

            migrationBuilder.DropColumn(
                name: "BackgroundId",
                table: "UserProfiles");

            migrationBuilder.DropColumn(
                name: "NickEffectId",
                table: "UserProfiles");

            migrationBuilder.DropColumn(
                name: "PrimaryColor",
                table: "UserProfiles");

            migrationBuilder.DropColumn(
                name: "VoiceCardEffectId",
                table: "UserProfiles");

            migrationBuilder.AddColumn<string>(
                name: "BannerFileId",
                table: "UserProfiles",
                type: "text",
                maxLength: 128,
                nullable: true);
        }
    }
}
