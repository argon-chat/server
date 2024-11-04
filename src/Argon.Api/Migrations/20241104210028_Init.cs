using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace Argon.Api.Migrations
{
    /// <inheritdoc />
    public partial class Init : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.CreateTable(
                name: "Servers",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "uuid", nullable: false),
                    CreatedAt = table.Column<DateTime>(type: "timestamp with time zone", nullable: false),
                    UpdatedAt = table.Column<DateTime>(type: "timestamp with time zone", nullable: false),
                    Name = table.Column<string>(type: "character varying(255)", maxLength: 255, nullable: false),
                    Description = table.Column<string>(type: "character varying(255)", maxLength: 255, nullable: true),
                    AvatarUrl = table.Column<string>(type: "character varying(255)", maxLength: 255, nullable: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_Servers", x => x.Id);
                });

            migrationBuilder.CreateTable(
                name: "Users",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "uuid", nullable: false),
                    CreatedAt = table.Column<DateTime>(type: "timestamp with time zone", nullable: false),
                    UpdatedAt = table.Column<DateTime>(type: "timestamp with time zone", nullable: false),
                    Email = table.Column<string>(type: "character varying(255)", maxLength: 255, nullable: false),
                    Username = table.Column<string>(type: "character varying(255)", maxLength: 255, nullable: true),
                    PhoneNumber = table.Column<string>(type: "character varying(30)", maxLength: 30, nullable: true),
                    PasswordDigest = table.Column<string>(type: "character varying(511)", maxLength: 511, nullable: true),
                    AvatarUrl = table.Column<string>(type: "character varying(1023)", maxLength: 1023, nullable: true),
                    OTP = table.Column<string>(type: "character varying(7)", maxLength: 7, nullable: true),
                    DeletedAt = table.Column<DateTime>(type: "timestamp with time zone", nullable: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_Users", x => x.Id);
                });

            migrationBuilder.CreateTable(
                name: "Channels",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "uuid", nullable: false),
                    CreatedAt = table.Column<DateTime>(type: "timestamp with time zone", nullable: false),
                    UpdatedAt = table.Column<DateTime>(type: "timestamp with time zone", nullable: false),
                    Name = table.Column<string>(type: "character varying(255)", maxLength: 255, nullable: false),
                    Description = table.Column<string>(type: "character varying(255)", maxLength: 255, nullable: false),
                    UserId = table.Column<Guid>(type: "uuid", nullable: false),
                    ChannelType = table.Column<int>(type: "integer", nullable: false),
                    AccessLevel = table.Column<int>(type: "integer", nullable: false),
                    ServerId = table.Column<Guid>(type: "uuid", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_Channels", x => x.Id);
                    table.ForeignKey(
                        name: "FK_Channels_Servers_ServerId",
                        column: x => x.ServerId,
                        principalTable: "Servers",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Cascade);
                });

            migrationBuilder.CreateTable(
                name: "UsersToServerRelations",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "uuid", nullable: false),
                    CreatedAt = table.Column<DateTime>(type: "timestamp with time zone", nullable: false),
                    UpdatedAt = table.Column<DateTime>(type: "timestamp with time zone", nullable: false),
                    ServerId = table.Column<Guid>(type: "uuid", nullable: false),
                    Role = table.Column<int>(type: "integer", nullable: false),
                    UserId = table.Column<Guid>(type: "uuid", nullable: false),
                    CustomUsername = table.Column<string>(type: "text", nullable: false),
                    IsBanned = table.Column<bool>(type: "boolean", nullable: false),
                    IsMuted = table.Column<bool>(type: "boolean", nullable: false),
                    BannedUntil = table.Column<DateTime>(type: "timestamp with time zone", nullable: true),
                    MutedUntil = table.Column<DateTime>(type: "timestamp with time zone", nullable: true),
                    AvatarUrl = table.Column<string>(type: "text", nullable: false),
                    CustomAvatarUrl = table.Column<string>(type: "text", nullable: true),
                    BanReason = table.Column<string>(type: "text", nullable: true),
                    MuteReason = table.Column<string>(type: "text", nullable: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_UsersToServerRelations", x => x.Id);
                    table.ForeignKey(
                        name: "FK_UsersToServerRelations_Servers_ServerId",
                        column: x => x.ServerId,
                        principalTable: "Servers",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Cascade);
                    table.ForeignKey(
                        name: "FK_UsersToServerRelations_Users_UserId",
                        column: x => x.UserId,
                        principalTable: "Users",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Cascade);
                });

            migrationBuilder.CreateIndex(
                name: "IX_Channels_ServerId",
                table: "Channels",
                column: "ServerId");

            migrationBuilder.CreateIndex(
                name: "IX_UsersToServerRelations_ServerId",
                table: "UsersToServerRelations",
                column: "ServerId");

            migrationBuilder.CreateIndex(
                name: "IX_UsersToServerRelations_UserId",
                table: "UsersToServerRelations",
                column: "UserId");
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropTable(
                name: "Channels");

            migrationBuilder.DropTable(
                name: "UsersToServerRelations");

            migrationBuilder.DropTable(
                name: "Servers");

            migrationBuilder.DropTable(
                name: "Users");
        }
    }
}
