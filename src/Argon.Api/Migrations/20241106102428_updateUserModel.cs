using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace Argon.Api.Migrations
{
    /// <inheritdoc />
    public partial class updateUserModel : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropColumn(
                name: "OTP",
                table: "Users");

            migrationBuilder.RenameColumn(
                name: "AvatarUrl",
                table: "Users",
                newName: "AvatarFileId");

            migrationBuilder.AddColumn<string>(
                name: "OtpHash",
                table: "Users",
                type: "character varying(128)",
                maxLength: 128,
                nullable: true);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropColumn(
                name: "OtpHash",
                table: "Users");

            migrationBuilder.RenameColumn(
                name: "AvatarFileId",
                table: "Users",
                newName: "AvatarUrl");

            migrationBuilder.AddColumn<string>(
                name: "OTP",
                table: "Users",
                type: "character varying(7)",
                maxLength: 7,
                nullable: true);
        }
    }
}
