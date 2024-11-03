using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace Argon.Api.Migrations
{
    /// <inheritdoc />
    public partial class FixedNullabilityOfUserFields : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AlterColumn<string>(
                name: "Username",
                table: "Users",
                type: "character varying(255)",
                maxLength: 255,
                nullable: true,
                oldClrType: typeof(string),
                oldType: "character varying(255)",
                oldMaxLength: 255);

            migrationBuilder.AlterColumn<string>(
                name: "PasswordDigest",
                table: "Users",
                type: "character varying(511)",
                maxLength: 511,
                nullable: true,
                oldClrType: typeof(string),
                oldType: "character varying(511)",
                oldMaxLength: 511);

            migrationBuilder.AlterColumn<string>(
                name: "OTP",
                table: "Users",
                type: "character varying(7)",
                maxLength: 7,
                nullable: true,
                oldClrType: typeof(string),
                oldType: "character varying(7)",
                oldMaxLength: 7);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AlterColumn<string>(
                name: "Username",
                table: "Users",
                type: "character varying(255)",
                maxLength: 255,
                nullable: false,
                defaultValue: "",
                oldClrType: typeof(string),
                oldType: "character varying(255)",
                oldMaxLength: 255,
                oldNullable: true);

            migrationBuilder.AlterColumn<string>(
                name: "PasswordDigest",
                table: "Users",
                type: "character varying(511)",
                maxLength: 511,
                nullable: false,
                defaultValue: "",
                oldClrType: typeof(string),
                oldType: "character varying(511)",
                oldMaxLength: 511,
                oldNullable: true);

            migrationBuilder.AlterColumn<string>(
                name: "OTP",
                table: "Users",
                type: "character varying(7)",
                maxLength: 7,
                nullable: false,
                defaultValue: "",
                oldClrType: typeof(string),
                oldType: "character varying(7)",
                oldMaxLength: 7,
                oldNullable: true);
        }
    }
}
