using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace Argon.Api.Migrations
{
    /// <inheritdoc />
    public partial class changesUserRelation : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropColumn(
                name: "AvatarUrl",
                table: "UsersToServerRelations");

            migrationBuilder.DropColumn(
                name: "BanReason",
                table: "UsersToServerRelations");

            migrationBuilder.DropColumn(
                name: "BannedUntil",
                table: "UsersToServerRelations");

            migrationBuilder.DropColumn(
                name: "CustomAvatarUrl",
                table: "UsersToServerRelations");

            migrationBuilder.DropColumn(
                name: "CustomUsername",
                table: "UsersToServerRelations");

            migrationBuilder.DropColumn(
                name: "IsBanned",
                table: "UsersToServerRelations");

            migrationBuilder.DropColumn(
                name: "IsMuted",
                table: "UsersToServerRelations");

            migrationBuilder.DropColumn(
                name: "MuteReason",
                table: "UsersToServerRelations");

            migrationBuilder.DropColumn(
                name: "MutedUntil",
                table: "UsersToServerRelations");
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<string>(
                name: "AvatarUrl",
                table: "UsersToServerRelations",
                type: "text",
                nullable: true);

            migrationBuilder.AddColumn<string>(
                name: "BanReason",
                table: "UsersToServerRelations",
                type: "text",
                nullable: true);

            migrationBuilder.AddColumn<DateTime>(
                name: "BannedUntil",
                table: "UsersToServerRelations",
                type: "timestamp with time zone",
                nullable: true);

            migrationBuilder.AddColumn<string>(
                name: "CustomAvatarUrl",
                table: "UsersToServerRelations",
                type: "text",
                nullable: true);

            migrationBuilder.AddColumn<string>(
                name: "CustomUsername",
                table: "UsersToServerRelations",
                type: "text",
                nullable: true);

            migrationBuilder.AddColumn<bool>(
                name: "IsBanned",
                table: "UsersToServerRelations",
                type: "boolean",
                nullable: false,
                defaultValue: false);

            migrationBuilder.AddColumn<bool>(
                name: "IsMuted",
                table: "UsersToServerRelations",
                type: "boolean",
                nullable: false,
                defaultValue: false);

            migrationBuilder.AddColumn<string>(
                name: "MuteReason",
                table: "UsersToServerRelations",
                type: "text",
                nullable: true);

            migrationBuilder.AddColumn<DateTime>(
                name: "MutedUntil",
                table: "UsersToServerRelations",
                type: "timestamp with time zone",
                nullable: true);
        }
    }
}
