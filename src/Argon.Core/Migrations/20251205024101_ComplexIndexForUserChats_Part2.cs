using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace Argon.Core.Migrations
{
    /// <inheritdoc />
    public partial class ComplexIndexForUserChats_Part2 : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
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
                column: "Id");
        }
    }
}
