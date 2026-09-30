using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace Argon.Core.Migrations
{
    /// <inheritdoc />
    public partial class AddUserSessions : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.CreateTable(
                name: "UserSessions",
                columns: table => new
                {
                    UserId = table.Column<Guid>(type: "uuid", nullable: false),
                    CredentialSessionId = table.Column<Guid>(type: "uuid", nullable: false),
                    PresenceSessionId = table.Column<Guid>(type: "uuid", nullable: true),
                    MachineId = table.Column<string>(type: "text", maxLength: 64, nullable: false),
                    ClientName = table.Column<string>(type: "text", maxLength: 512, nullable: false),
                    Region = table.Column<string>(type: "text", maxLength: 8, nullable: false),
                    City = table.Column<string>(type: "text", maxLength: 128, nullable: false),
                    Ip = table.Column<string>(type: "text", maxLength: 64, nullable: false),
                    AppId = table.Column<string>(type: "text", maxLength: 64, nullable: false),
                    AppName = table.Column<string>(type: "text", maxLength: 128, nullable: false),
                    AppVersion = table.Column<string>(type: "text", maxLength: 64, nullable: false),
                    Platform = table.Column<int>(type: "integer", nullable: false),
                    OsName = table.Column<string>(type: "text", maxLength: 128, nullable: false),
                    DeviceName = table.Column<string>(type: "text", maxLength: 128, nullable: false),
                    CreatedAt = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false),
                    LastSeenAt = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_UserSessions", x => new { x.UserId, x.CredentialSessionId });
                    table.ForeignKey(
                        name: "FK_UserSessions_Users_UserId",
                        column: x => x.UserId,
                        principalTable: "Users",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Cascade);
                });

            migrationBuilder.CreateIndex(
                name: "IX_UserSessions_LastSeenAt",
                table: "UserSessions",
                column: "LastSeenAt");

            migrationBuilder.CreateIndex(
                name: "IX_UserSessions_UserId",
                table: "UserSessions",
                column: "UserId");
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropTable(
                name: "UserSessions");
        }
    }
}
