using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace Argon.Core.Migrations
{
    /// <inheritdoc />
    public partial class AddUserConnections : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.CreateTable(
                name: "ConnectionTrophyGrants",
                columns: table => new
                {
                    Provider = table.Column<int>(type: "integer", nullable: false),
                    ExternalId = table.Column<string>(type: "text", maxLength: 128, nullable: false),
                    TrophyId = table.Column<string>(type: "text", maxLength: 128, nullable: false),
                    UserId = table.Column<Guid>(type: "uuid", nullable: false),
                    GrantedAt = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_ConnectionTrophyGrants", x => new { x.Provider, x.ExternalId, x.TrophyId });
                });

            migrationBuilder.CreateTable(
                name: "UserConnections",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "uuid", nullable: false),
                    UserId = table.Column<Guid>(type: "uuid", nullable: false),
                    Provider = table.Column<int>(type: "integer", nullable: false),
                    ExternalId = table.Column<string>(type: "varchar(128)", maxLength: 128, nullable: false),
                    ExternalName = table.Column<string>(type: "text", maxLength: 128, nullable: false),
                    ExternalUrl = table.Column<string>(type: "text", maxLength: 512, nullable: true),
                    AvatarUrl = table.Column<string>(type: "text", maxLength: 1024, nullable: true),
                    DisplayOnProfile = table.Column<bool>(type: "boolean", nullable: false),
                    ShowDetails = table.Column<bool>(type: "boolean", nullable: false),
                    DisplayAsStatus = table.Column<bool>(type: "boolean", nullable: false),
                    AllowListenAlong = table.Column<bool>(type: "boolean", nullable: false),
                    Scopes = table.Column<string>(type: "text", maxLength: 1024, nullable: false),
                    SealedTokens = table.Column<byte[]>(type: "bytea", nullable: true),
                    TokenKeyVersion = table.Column<int>(type: "integer", nullable: false),
                    AccessTokenExpiresAt = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: true),
                    Details = table.Column<string>(type: "jsonb", nullable: false),
                    DetailsRefreshedAt = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: true),
                    Status = table.Column<int>(type: "integer", nullable: false),
                    LastError = table.Column<string>(type: "text", maxLength: 512, nullable: true),
                    LastErrorAt = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: true),
                    CreatedAt = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false),
                    UpdatedAt = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_UserConnections", x => x.Id);
                    table.ForeignKey(
                        name: "FK_UserConnections_Users_UserId",
                        column: x => x.UserId,
                        principalTable: "Users",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Cascade);
                });

            migrationBuilder.CreateIndex(
                name: "IX_ConnectionTrophyGrants_UserId",
                table: "ConnectionTrophyGrants",
                column: "UserId");

            migrationBuilder.CreateIndex(
                name: "IX_UserConnections_AccessTokenExpiresAt",
                table: "UserConnections",
                column: "AccessTokenExpiresAt");

            migrationBuilder.CreateIndex(
                name: "IX_UserConnections_DetailsRefreshedAt",
                table: "UserConnections",
                column: "DetailsRefreshedAt");

            migrationBuilder.CreateIndex(
                name: "IX_UserConnections_Provider_ExternalId",
                table: "UserConnections",
                columns: new[] { "Provider", "ExternalId" },
                unique: true);

            migrationBuilder.CreateIndex(
                name: "IX_UserConnections_UserId_Provider",
                table: "UserConnections",
                columns: new[] { "UserId", "Provider" },
                unique: true);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropTable(
                name: "ConnectionTrophyGrants");

            migrationBuilder.DropTable(
                name: "UserConnections");
        }
    }
}
