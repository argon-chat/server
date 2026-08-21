using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace Argon.Core.Migrations
{
    /// <inheritdoc />
    public partial class InvitesOfTeamAndFixesIndex : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropIndex(
                name: "IX_UsersToServerRelations_UserId",
                table: "UsersToServerRelations");

            migrationBuilder.AddColumn<string>(
                name: "Name",
                table: "DevTeamEntity",
                type: "text",
                nullable: false,
                defaultValue: "");

            migrationBuilder.CreateTable(
                name: "TeamInvites",
                columns: table => new
                {
                    TeamId = table.Column<Guid>(type: "uuid", nullable: false),
                    ToUserId = table.Column<Guid>(type: "uuid", nullable: false),
                    FromUserId = table.Column<Guid>(type: "uuid", nullable: false),
                    CreatedAt = table.Column<DateTime>(type: "timestamp with time zone", nullable: false),
                    Accepted = table.Column<bool>(type: "boolean", nullable: false),
                    Revoked = table.Column<bool>(type: "boolean", nullable: false),
                    ExpireAt = table.Column<DateTimeOffset>(type: "TIMESTAMPTZ", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_TeamInvites", x => new { x.TeamId, x.ToUserId });
                    table.ForeignKey(
                        name: "FK_TeamInvites_DevTeamEntity_TeamId",
                        column: x => x.TeamId,
                        principalTable: "DevTeamEntity",
                        principalColumn: "TeamId",
                        onDelete: ReferentialAction.Cascade);
                });

            migrationBuilder.CreateIndex(
                name: "IX_UsersToServerRelations_UserId",
                table: "UsersToServerRelations",
                column: "UserId")
                .Annotation("Npgsql:CreatedConcurrently", true)
                .Annotation("Npgsql:IndexInclude", new[] { "SpaceId", "IsDeleted" });

            migrationBuilder.CreateIndex(
                name: "IX_TeamInvites_TeamId_ToUserId",
                table: "TeamInvites",
                columns: new[] { "TeamId", "ToUserId" },
                unique: true);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropTable(
                name: "TeamInvites");

            migrationBuilder.DropIndex(
                name: "IX_UsersToServerRelations_UserId",
                table: "UsersToServerRelations");

            migrationBuilder.DropColumn(
                name: "Name",
                table: "DevTeamEntity");

            migrationBuilder.CreateIndex(
                name: "IX_UsersToServerRelations_UserId",
                table: "UsersToServerRelations",
                column: "UserId");
        }
    }
}
