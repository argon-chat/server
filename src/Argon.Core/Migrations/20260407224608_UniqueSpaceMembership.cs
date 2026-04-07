using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace Argon.Core.Migrations
{
    /// <inheritdoc />
    public partial class UniqueSpaceMembership : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropIndex(
                name: "IX_UsersToServerRelations_SpaceId",
                table: "UsersToServerRelations");

            migrationBuilder.CreateIndex(
                name: "IX_UsersToServerRelations_SpaceId_UserId",
                table: "UsersToServerRelations",
                columns: new[] { "SpaceId", "UserId" },
                unique: true,
                filter: "\"IsDeleted\" = false")
                .Annotation("Npgsql:CreatedConcurrently", true);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropIndex(
                name: "IX_UsersToServerRelations_SpaceId_UserId",
                table: "UsersToServerRelations");

            migrationBuilder.CreateIndex(
                name: "IX_UsersToServerRelations_SpaceId",
                table: "UsersToServerRelations",
                column: "SpaceId");
        }
    }
}
