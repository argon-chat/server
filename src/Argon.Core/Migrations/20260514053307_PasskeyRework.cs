using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace Argon.Core.Migrations
{
    /// <inheritdoc />
    public partial class PasskeyRework : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropColumn(
                name: "Challenge",
                table: "Passkeys");

            migrationBuilder.AlterColumn<byte[]>(
                name: "PublicKey",
                table: "Passkeys",
                type: "bytea",
                maxLength: 2048,
                nullable: true,
                oldClrType: typeof(string),
                oldType: "text",
                oldMaxLength: 1024,
                oldNullable: true);

            migrationBuilder.AddColumn<Guid>(
                name: "AaGuid",
                table: "Passkeys",
                type: "uuid",
                nullable: true);

            migrationBuilder.AddColumn<byte[]>(
                name: "CredentialId",
                table: "Passkeys",
                type: "bytea",
                maxLength: 1024,
                nullable: true);

            migrationBuilder.AddColumn<long>(
                name: "SignCount",
                table: "Passkeys",
                type: "bigint",
                nullable: false,
                defaultValue: 0L);

            migrationBuilder.CreateIndex(
                name: "IX_Passkeys_CredentialId",
                table: "Passkeys",
                column: "CredentialId");
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropIndex(
                name: "IX_Passkeys_CredentialId",
                table: "Passkeys");

            migrationBuilder.DropColumn(
                name: "AaGuid",
                table: "Passkeys");

            migrationBuilder.DropColumn(
                name: "CredentialId",
                table: "Passkeys");

            migrationBuilder.DropColumn(
                name: "SignCount",
                table: "Passkeys");

            migrationBuilder.AlterColumn<string>(
                name: "PublicKey",
                table: "Passkeys",
                type: "text",
                maxLength: 1024,
                nullable: true,
                oldClrType: typeof(byte[]),
                oldType: "bytea",
                oldMaxLength: 2048,
                oldNullable: true);

            migrationBuilder.AddColumn<string>(
                name: "Challenge",
                table: "Passkeys",
                type: "text",
                maxLength: 512,
                nullable: true);
        }
    }
}
