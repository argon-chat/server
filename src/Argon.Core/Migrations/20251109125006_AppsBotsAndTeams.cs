using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace Argon.Core.Migrations
{
    /// <inheritdoc />
    public partial class AppsBotsAndTeams : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.CreateTable(
                name: "DevTeamEntity",
                columns: table => new
                {
                    TeamId = table.Column<Guid>(type: "uuid", nullable: false),
                    OwnerId = table.Column<Guid>(type: "uuid", nullable: false),
                    AvatarFileId = table.Column<string>(type: "text", nullable: true),
                    CreatedAt = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false),
                    UpdatedAt = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false),
                    DeletedAt = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: true),
                    IsDeleted = table.Column<bool>(type: "boolean", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_DevTeamEntity", x => x.TeamId);
                    table.ForeignKey(
                        name: "FK_DevTeamEntity_Users_OwnerId",
                        column: x => x.OwnerId,
                        principalTable: "Users",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Cascade);
                });

            migrationBuilder.CreateTable(
                name: "DevApps",
                columns: table => new
                {
                    AppId = table.Column<Guid>(type: "uuid", nullable: false),
                    TeamId = table.Column<Guid>(type: "uuid", nullable: false),
                    Name = table.Column<string>(type: "text", nullable: false),
                    Description = table.Column<string>(type: "text", nullable: true),
                    ClientId = table.Column<string>(type: "varchar(256)", maxLength: 256, nullable: false),
                    ClientSecret = table.Column<string>(type: "text", nullable: false),
                    VerificationKey = table.Column<string>(type: "text", nullable: true),
                    AppType = table.Column<int>(type: "integer", nullable: false),
                    CreatedAt = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false),
                    UpdatedAt = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false),
                    DeletedAt = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: true),
                    IsDeleted = table.Column<bool>(type: "boolean", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_DevApps", x => x.AppId);
                    table.ForeignKey(
                        name: "FK_DevApps_DevTeamEntity_TeamId",
                        column: x => x.TeamId,
                        principalTable: "DevTeamEntity",
                        principalColumn: "TeamId",
                        onDelete: ReferentialAction.Cascade);
                });

            migrationBuilder.CreateTable(
                name: "MemberTeamEntities",
                columns: table => new
                {
                    TeamId = table.Column<Guid>(type: "uuid", nullable: false),
                    UserId = table.Column<Guid>(type: "uuid", nullable: false),
                    JoinedAt = table.Column<DateTime>(type: "timestamp with time zone", nullable: false),
                    IsPending = table.Column<bool>(type: "boolean", nullable: false),
                    IsOwner = table.Column<bool>(type: "boolean", nullable: false),
                    Claims = table.Column<string>(type: "jsonb", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_MemberTeamEntities", x => new { x.TeamId, x.UserId });
                    table.ForeignKey(
                        name: "FK_MemberTeamEntities_DevTeamEntity_TeamId",
                        column: x => x.TeamId,
                        principalTable: "DevTeamEntity",
                        principalColumn: "TeamId",
                        onDelete: ReferentialAction.Cascade);
                    table.ForeignKey(
                        name: "FK_MemberTeamEntities_Users_UserId",
                        column: x => x.UserId,
                        principalTable: "Users",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Cascade);
                });

            migrationBuilder.CreateTable(
                name: "Bots",
                columns: table => new
                {
                    AppId = table.Column<Guid>(type: "uuid", nullable: false),
                    BotToken = table.Column<string>(type: "text", nullable: false),
                    RequiresOAuth2 = table.Column<bool>(type: "boolean", nullable: false),
                    IsPublic = table.Column<bool>(type: "boolean", nullable: false),
                    AllowDMs = table.Column<bool>(type: "boolean", nullable: false),
                    IsVerified = table.Column<bool>(type: "boolean", nullable: false),
                    IsRestricted = table.Column<bool>(type: "boolean", nullable: false),
                    MaxSpaces = table.Column<int>(type: "integer", nullable: false),
                    BotAsUserId = table.Column<Guid>(type: "uuid", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_Bots", x => x.AppId);
                    table.ForeignKey(
                        name: "FK_Bots_DevApps_AppId",
                        column: x => x.AppId,
                        principalTable: "DevApps",
                        principalColumn: "AppId",
                        onDelete: ReferentialAction.Cascade);
                    table.ForeignKey(
                        name: "FK_Bots_Users_BotAsUserId",
                        column: x => x.BotAsUserId,
                        principalTable: "Users",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Cascade);
                });

            migrationBuilder.CreateIndex(
                name: "IX_Bots_BotAsUserId",
                table: "Bots",
                column: "BotAsUserId",
                unique: true);

            migrationBuilder.CreateIndex(
                name: "IX_DevApps_ClientId",
                table: "DevApps",
                column: "ClientId",
                unique: true);

            migrationBuilder.CreateIndex(
                name: "IX_DevApps_TeamId",
                table: "DevApps",
                column: "TeamId");

            migrationBuilder.CreateIndex(
                name: "IX_DevTeamEntity_OwnerId",
                table: "DevTeamEntity",
                column: "OwnerId");

            migrationBuilder.CreateIndex(
                name: "IX_MemberTeamEntities_UserId",
                table: "MemberTeamEntities",
                column: "UserId");
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropTable(
                name: "Bots");

            migrationBuilder.DropTable(
                name: "MemberTeamEntities");

            migrationBuilder.DropTable(
                name: "DevApps");

            migrationBuilder.DropTable(
                name: "DevTeamEntity");
        }
    }
}
