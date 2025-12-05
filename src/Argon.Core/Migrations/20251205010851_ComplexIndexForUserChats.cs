using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace Argon.Core.Migrations
{
    /// <inheritdoc />
    public partial class ComplexIndexForUserChats : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropPrimaryKey(
                name: "PK_user_chats",
                table: "user_chats");

            migrationBuilder.DropIndex(
                name: "ux_user_chats_unique",
                table: "user_chats");

            migrationBuilder.AddPrimaryKey(
                name: "PK_user_chats",
                table: "user_chats",
                columns: new[] { "UserId", "PeerId" });
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropPrimaryKey(
                name: "PK_user_chats",
                table: "user_chats");

            migrationBuilder.AddPrimaryKey(
                name: "PK_user_chats",
                table: "user_chats",
                column: "PeerId");

            migrationBuilder.CreateIndex(
                name: "ux_user_chats_unique",
                table: "user_chats",
                columns: new[] { "UserId", "PeerId" },
                unique: true);
        }
    }
}
