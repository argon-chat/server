using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace Argon.Api.Migrations
{
    /// <inheritdoc />
    public partial class Fixes : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.RenameColumn(
                name: "ChannelId",
                table: "Channels",
                newName: "ServerId");

            migrationBuilder.AlterColumn<string>(
                name: "MuteReason",
                table: "UsersToServerRelations",
                type: "text",
                nullable: true,
                oldClrType: typeof(string),
                oldType: "character varying(255)",
                oldMaxLength: 255,
                oldNullable: true);

            migrationBuilder.AlterColumn<string>(
                name: "CustomUsername",
                table: "UsersToServerRelations",
                type: "text",
                nullable: false,
                oldClrType: typeof(string),
                oldType: "character varying(255)",
                oldMaxLength: 255);

            migrationBuilder.AlterColumn<string>(
                name: "CustomAvatarUrl",
                table: "UsersToServerRelations",
                type: "text",
                nullable: true,
                oldClrType: typeof(string),
                oldType: "character varying(255)",
                oldMaxLength: 255,
                oldNullable: true);

            migrationBuilder.AlterColumn<string>(
                name: "BanReason",
                table: "UsersToServerRelations",
                type: "text",
                nullable: true,
                oldClrType: typeof(string),
                oldType: "character varying(255)",
                oldMaxLength: 255,
                oldNullable: true);

            migrationBuilder.AlterColumn<string>(
                name: "AvatarUrl",
                table: "UsersToServerRelations",
                type: "text",
                nullable: false,
                oldClrType: typeof(string),
                oldType: "character varying(255)",
                oldMaxLength: 255);

            migrationBuilder.CreateIndex(
                name: "IX_UsersToServerRelations_ServerId",
                table: "UsersToServerRelations",
                column: "ServerId");

            migrationBuilder.CreateIndex(
                name: "IX_UsersToServerRelations_UserId",
                table: "UsersToServerRelations",
                column: "UserId");

            migrationBuilder.CreateIndex(
                name: "IX_Channels_ServerId",
                table: "Channels",
                column: "ServerId");

            migrationBuilder.AddForeignKey(
                name: "FK_Channels_Servers_ServerId",
                table: "Channels",
                column: "ServerId",
                principalTable: "Servers",
                principalColumn: "Id",
                onDelete: ReferentialAction.Cascade);

            migrationBuilder.AddForeignKey(
                name: "FK_UsersToServerRelations_Servers_ServerId",
                table: "UsersToServerRelations",
                column: "ServerId",
                principalTable: "Servers",
                principalColumn: "Id",
                onDelete: ReferentialAction.Cascade);

            migrationBuilder.AddForeignKey(
                name: "FK_UsersToServerRelations_Users_UserId",
                table: "UsersToServerRelations",
                column: "UserId",
                principalTable: "Users",
                principalColumn: "Id",
                onDelete: ReferentialAction.Cascade);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropForeignKey(
                name: "FK_Channels_Servers_ServerId",
                table: "Channels");

            migrationBuilder.DropForeignKey(
                name: "FK_UsersToServerRelations_Servers_ServerId",
                table: "UsersToServerRelations");

            migrationBuilder.DropForeignKey(
                name: "FK_UsersToServerRelations_Users_UserId",
                table: "UsersToServerRelations");

            migrationBuilder.DropIndex(
                name: "IX_UsersToServerRelations_ServerId",
                table: "UsersToServerRelations");

            migrationBuilder.DropIndex(
                name: "IX_UsersToServerRelations_UserId",
                table: "UsersToServerRelations");

            migrationBuilder.DropIndex(
                name: "IX_Channels_ServerId",
                table: "Channels");

            migrationBuilder.RenameColumn(
                name: "ServerId",
                table: "Channels",
                newName: "ChannelId");

            migrationBuilder.AlterColumn<string>(
                name: "MuteReason",
                table: "UsersToServerRelations",
                type: "character varying(255)",
                maxLength: 255,
                nullable: true,
                oldClrType: typeof(string),
                oldType: "text",
                oldNullable: true);

            migrationBuilder.AlterColumn<string>(
                name: "CustomUsername",
                table: "UsersToServerRelations",
                type: "character varying(255)",
                maxLength: 255,
                nullable: false,
                oldClrType: typeof(string),
                oldType: "text");

            migrationBuilder.AlterColumn<string>(
                name: "CustomAvatarUrl",
                table: "UsersToServerRelations",
                type: "character varying(255)",
                maxLength: 255,
                nullable: true,
                oldClrType: typeof(string),
                oldType: "text",
                oldNullable: true);

            migrationBuilder.AlterColumn<string>(
                name: "BanReason",
                table: "UsersToServerRelations",
                type: "character varying(255)",
                maxLength: 255,
                nullable: true,
                oldClrType: typeof(string),
                oldType: "text",
                oldNullable: true);

            migrationBuilder.AlterColumn<string>(
                name: "AvatarUrl",
                table: "UsersToServerRelations",
                type: "character varying(255)",
                maxLength: 255,
                nullable: false,
                oldClrType: typeof(string),
                oldType: "text");
        }
    }
}
