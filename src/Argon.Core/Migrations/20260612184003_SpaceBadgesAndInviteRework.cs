using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace Argon.Core.Migrations
{
    /// <inheritdoc />
    public partial class SpaceBadgesAndInviteRework : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<long>(
                name: "JoinedViaInviteId",
                table: "UsersToServerRelations",
                type: "bigint",
                nullable: true);

            migrationBuilder.AddColumn<bool>(
                name: "HideBoostStrip",
                table: "Spaces",
                type: "boolean",
                nullable: false,
                defaultValue: false);

            migrationBuilder.AddColumn<string>(
                name: "InviteImageFileId",
                table: "Spaces",
                type: "text",
                maxLength: 128,
                nullable: true);

            migrationBuilder.AddColumn<bool>(
                name: "IsOfficial",
                table: "Spaces",
                type: "boolean",
                nullable: false,
                defaultValue: false);

            migrationBuilder.AddColumn<bool>(
                name: "IsVerified",
                table: "Spaces",
                type: "boolean",
                nullable: false,
                defaultValue: false);

            migrationBuilder.AddColumn<int>(
                name: "MaxUses",
                table: "Invites",
                type: "integer",
                nullable: false,
                defaultValue: 0);

            migrationBuilder.AddColumn<long>(
                name: "UsedCount",
                table: "Invites",
                type: "bigint",
                nullable: false,
                defaultValue: 0L);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropColumn(
                name: "JoinedViaInviteId",
                table: "UsersToServerRelations");

            migrationBuilder.DropColumn(
                name: "HideBoostStrip",
                table: "Spaces");

            migrationBuilder.DropColumn(
                name: "InviteImageFileId",
                table: "Spaces");

            migrationBuilder.DropColumn(
                name: "IsOfficial",
                table: "Spaces");

            migrationBuilder.DropColumn(
                name: "IsVerified",
                table: "Spaces");

            migrationBuilder.DropColumn(
                name: "MaxUses",
                table: "Invites");

            migrationBuilder.DropColumn(
                name: "UsedCount",
                table: "Invites");
        }
    }
}
