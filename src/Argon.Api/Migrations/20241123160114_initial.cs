using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

#pragma warning disable CA1814 // Prefer jagged arrays over multidimensional

namespace Argon.Api.Migrations
{
    /// <inheritdoc />
    public partial class initial : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            var sql = System.IO.File.ReadAllText("Migrations/orleans_up.sql");
            migrationBuilder.Sql(sql);

            migrationBuilder.CreateTable(
                name: "Servers",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "uuid", nullable: false),
                    Name = table.Column<string>(type: "character varying(64)", maxLength: 64, nullable: false),
                    Description = table.Column<string>(type: "character varying(1024)", maxLength: 1024, nullable: true),
                    AvatarFileId = table.Column<string>(type: "character varying(128)", maxLength: 128, nullable: true),
                    CreatedAt = table.Column<DateTime>(type: "timestamp with time zone", nullable: false),
                    UpdatedAt = table.Column<DateTime>(type: "timestamp with time zone", nullable: false),
                    DeletedAt = table.Column<DateTime>(type: "timestamp with time zone", nullable: true),
                    IsDeleted = table.Column<bool>(type: "boolean", nullable: false),
                    CreatorId = table.Column<Guid>(type: "uuid", nullable: false)
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
                    Email = table.Column<string>(type: "character varying(255)", maxLength: 255, nullable: false),
                    Username = table.Column<string>(type: "character varying(64)", maxLength: 64, nullable: false),
                    DisplayName = table.Column<string>(type: "character varying(64)", maxLength: 64, nullable: false),
                    PhoneNumber = table.Column<string>(type: "character varying(64)", maxLength: 64, nullable: true),
                    PasswordDigest = table.Column<string>(type: "character varying(512)", maxLength: 512, nullable: true),
                    AvatarFileId = table.Column<string>(type: "character varying(128)", maxLength: 128, nullable: true),
                    OtpHash = table.Column<string>(type: "character varying(128)", maxLength: 128, nullable: true),
                    CreatedAt = table.Column<DateTime>(type: "timestamp with time zone", nullable: false),
                    UpdatedAt = table.Column<DateTime>(type: "timestamp with time zone", nullable: false),
                    DeletedAt = table.Column<DateTime>(type: "timestamp with time zone", nullable: true),
                    IsDeleted = table.Column<bool>(type: "boolean", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_Users", x => x.Id);
                });

            migrationBuilder.CreateTable(
                name: "Archetypes",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "uuid", nullable: false),
                    ServerId = table.Column<Guid>(type: "uuid", nullable: false),
                    Name = table.Column<string>(type: "character varying(64)", maxLength: 64, nullable: false),
                    Description = table.Column<string>(type: "character varying(512)", maxLength: 512, nullable: false),
                    Entitlement = table.Column<decimal>(type: "numeric(20,0)", nullable: false),
                    IsMentionable = table.Column<bool>(type: "boolean", nullable: false),
                    IsLocked = table.Column<bool>(type: "boolean", nullable: false),
                    IsHidden = table.Column<bool>(type: "boolean", nullable: false),
                    Colour = table.Column<int>(type: "integer", nullable: false),
                    IconFileId = table.Column<string>(type: "character varying(128)", maxLength: 128, nullable: true),
                    CreatedAt = table.Column<DateTime>(type: "timestamp with time zone", nullable: false),
                    UpdatedAt = table.Column<DateTime>(type: "timestamp with time zone", nullable: false),
                    DeletedAt = table.Column<DateTime>(type: "timestamp with time zone", nullable: true),
                    IsDeleted = table.Column<bool>(type: "boolean", nullable: false),
                    CreatorId = table.Column<Guid>(type: "uuid", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_Archetypes", x => x.Id);
                    table.ForeignKey(
                        name: "FK_Archetypes_Servers_ServerId",
                        column: x => x.ServerId,
                        principalTable: "Servers",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Cascade);
                });

            migrationBuilder.CreateTable(
                name: "Channels",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "uuid", nullable: false),
                    ChannelType = table.Column<int>(type: "integer", nullable: false),
                    ServerId = table.Column<Guid>(type: "uuid", nullable: false),
                    Name = table.Column<string>(type: "character varying(128)", maxLength: 128, nullable: false),
                    Description = table.Column<string>(type: "character varying(1024)", maxLength: 1024, nullable: true),
                    CreatedAt = table.Column<DateTime>(type: "timestamp with time zone", nullable: false),
                    UpdatedAt = table.Column<DateTime>(type: "timestamp with time zone", nullable: false),
                    DeletedAt = table.Column<DateTime>(type: "timestamp with time zone", nullable: true),
                    IsDeleted = table.Column<bool>(type: "boolean", nullable: false),
                    CreatorId = table.Column<Guid>(type: "uuid", nullable: false)
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
                name: "UserAgreements",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "uuid", nullable: false),
                    AllowedSendOptionalEmails = table.Column<bool>(type: "boolean", nullable: false),
                    AgreeTOS = table.Column<bool>(type: "boolean", nullable: false),
                    CreatedAt = table.Column<DateTime>(type: "timestamp with time zone", nullable: false),
                    UpdatedAt = table.Column<DateTime>(type: "timestamp with time zone", nullable: false),
                    UserId = table.Column<Guid>(type: "uuid", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_UserAgreements", x => x.Id);
                    table.ForeignKey(
                        name: "FK_UserAgreements_Users_UserId",
                        column: x => x.UserId,
                        principalTable: "Users",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Cascade);
                });

            migrationBuilder.CreateTable(
                name: "UsersToServerRelations",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "uuid", nullable: false),
                    ServerId = table.Column<Guid>(type: "uuid", nullable: false),
                    UserId = table.Column<Guid>(type: "uuid", nullable: false),
                    JoinedAt = table.Column<DateTime>(type: "timestamp with time zone", nullable: false),
                    CreatedAt = table.Column<DateTime>(type: "timestamp with time zone", nullable: false),
                    UpdatedAt = table.Column<DateTime>(type: "timestamp with time zone", nullable: false),
                    DeletedAt = table.Column<DateTime>(type: "timestamp with time zone", nullable: true),
                    IsDeleted = table.Column<bool>(type: "boolean", nullable: false),
                    CreatorId = table.Column<Guid>(type: "uuid", nullable: false)
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

            migrationBuilder.CreateTable(
                name: "ChannelEntitlementOverwrites",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "uuid", nullable: false),
                    ChannelId = table.Column<Guid>(type: "uuid", nullable: false),
                    Scope = table.Column<int>(type: "integer", nullable: false),
                    ArchetypeId = table.Column<Guid>(type: "uuid", nullable: true),
                    ServerMemberId = table.Column<Guid>(type: "uuid", nullable: true),
                    Allow = table.Column<decimal>(type: "numeric(20,0)", nullable: false),
                    Deny = table.Column<decimal>(type: "numeric(20,0)", nullable: false),
                    CreatedAt = table.Column<DateTime>(type: "timestamp with time zone", nullable: false),
                    UpdatedAt = table.Column<DateTime>(type: "timestamp with time zone", nullable: false),
                    DeletedAt = table.Column<DateTime>(type: "timestamp with time zone", nullable: true),
                    IsDeleted = table.Column<bool>(type: "boolean", nullable: false),
                    CreatorId = table.Column<Guid>(type: "uuid", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_ChannelEntitlementOverwrites", x => x.Id);
                    table.ForeignKey(
                        name: "FK_ChannelEntitlementOverwrites_Archetypes_ArchetypeId",
                        column: x => x.ArchetypeId,
                        principalTable: "Archetypes",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Restrict);
                    table.ForeignKey(
                        name: "FK_ChannelEntitlementOverwrites_Channels_ChannelId",
                        column: x => x.ChannelId,
                        principalTable: "Channels",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Cascade);
                    table.ForeignKey(
                        name: "FK_ChannelEntitlementOverwrites_UsersToServerRelations_ServerM~",
                        column: x => x.ServerMemberId,
                        principalTable: "UsersToServerRelations",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Restrict);
                });

            migrationBuilder.CreateTable(
                name: "ServerMemberArchetypes",
                columns: table => new
                {
                    ServerMemberId = table.Column<Guid>(type: "uuid", nullable: false),
                    ArchetypeId = table.Column<Guid>(type: "uuid", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_ServerMemberArchetypes", x => new { x.ServerMemberId, x.ArchetypeId });
                    table.ForeignKey(
                        name: "FK_ServerMemberArchetypes_Archetypes_ArchetypeId",
                        column: x => x.ArchetypeId,
                        principalTable: "Archetypes",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Cascade);
                    table.ForeignKey(
                        name: "FK_ServerMemberArchetypes_UsersToServerRelations_ServerMemberId",
                        column: x => x.ServerMemberId,
                        principalTable: "UsersToServerRelations",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Cascade);
                });

            migrationBuilder.InsertData(
                table: "Servers",
                columns: new[] { "Id", "AvatarFileId", "CreatedAt", "CreatorId", "DeletedAt", "Description", "IsDeleted", "Name", "UpdatedAt" },
                values: new object[] { new Guid("11111111-0000-1111-1111-111111111111"), "", new DateTime(1, 1, 1, 0, 0, 0, 0, DateTimeKind.Unspecified), new Guid("11111111-2222-1111-2222-111111111111"), null, "", false, "system_server", new DateTime(1, 1, 1, 0, 0, 0, 0, DateTimeKind.Unspecified) });

            migrationBuilder.InsertData(
                table: "Users",
                columns: new[] { "Id", "AvatarFileId", "CreatedAt", "DeletedAt", "DisplayName", "Email", "IsDeleted", "OtpHash", "PasswordDigest", "PhoneNumber", "UpdatedAt", "Username" },
                values: new object[] { new Guid("11111111-2222-1111-2222-111111111111"), null, new DateTime(1, 1, 1, 0, 0, 0, 0, DateTimeKind.Unspecified), null, "System", "system@argon.gl", false, null, null, null, new DateTime(1, 1, 1, 0, 0, 0, 0, DateTimeKind.Unspecified), "system" });

            migrationBuilder.InsertData(
                table: "Archetypes",
                columns: new[] { "Id", "Colour", "CreatedAt", "CreatorId", "DeletedAt", "Description", "Entitlement", "IconFileId", "IsDeleted", "IsHidden", "IsLocked", "IsMentionable", "Name", "ServerId", "UpdatedAt" },
                values: new object[,]
                {
                    { new Guid("11111111-3333-0000-1111-111111111111"), -8355712, new DateTime(2024, 11, 23, 16, 1, 14, 205, DateTimeKind.Utc).AddTicks(8382), new Guid("11111111-2222-1111-2222-111111111111"), null, "Default role for everyone in this server", 15760355m, null, false, false, false, true, "everyone", new Guid("11111111-0000-1111-1111-111111111111"), new DateTime(1, 1, 1, 0, 0, 0, 0, DateTimeKind.Unspecified) },
                    { new Guid("11111111-4444-0000-1111-111111111111"), -8355712, new DateTime(2024, 11, 23, 16, 1, 14, 205, DateTimeKind.Utc).AddTicks(8411), new Guid("11111111-2222-1111-2222-111111111111"), null, "Default role for owner in this server", -1m, null, false, true, true, false, "owner", new Guid("11111111-0000-1111-1111-111111111111"), new DateTime(1, 1, 1, 0, 0, 0, 0, DateTimeKind.Unspecified) }
                });

            migrationBuilder.CreateIndex(
                name: "IX_Archetypes_CreatorId",
                table: "Archetypes",
                column: "CreatorId");

            migrationBuilder.CreateIndex(
                name: "IX_Archetypes_ServerId",
                table: "Archetypes",
                column: "ServerId");

            migrationBuilder.CreateIndex(
                name: "IX_ChannelEntitlementOverwrites_ArchetypeId",
                table: "ChannelEntitlementOverwrites",
                column: "ArchetypeId");

            migrationBuilder.CreateIndex(
                name: "IX_ChannelEntitlementOverwrites_ChannelId",
                table: "ChannelEntitlementOverwrites",
                column: "ChannelId");

            migrationBuilder.CreateIndex(
                name: "IX_ChannelEntitlementOverwrites_CreatorId",
                table: "ChannelEntitlementOverwrites",
                column: "CreatorId");

            migrationBuilder.CreateIndex(
                name: "IX_ChannelEntitlementOverwrites_ServerMemberId",
                table: "ChannelEntitlementOverwrites",
                column: "ServerMemberId");

            migrationBuilder.CreateIndex(
                name: "IX_Channels_CreatorId",
                table: "Channels",
                column: "CreatorId");

            migrationBuilder.CreateIndex(
                name: "IX_Channels_Id_ServerId",
                table: "Channels",
                columns: new[] { "Id", "ServerId" });

            migrationBuilder.CreateIndex(
                name: "IX_Channels_ServerId",
                table: "Channels",
                column: "ServerId");

            migrationBuilder.CreateIndex(
                name: "IX_ServerMemberArchetypes_ArchetypeId",
                table: "ServerMemberArchetypes",
                column: "ArchetypeId");

            migrationBuilder.CreateIndex(
                name: "IX_Servers_CreatorId",
                table: "Servers",
                column: "CreatorId");

            migrationBuilder.CreateIndex(
                name: "IX_UserAgreements_UserId",
                table: "UserAgreements",
                column: "UserId");

            migrationBuilder.CreateIndex(
                name: "IX_UsersToServerRelations_CreatorId",
                table: "UsersToServerRelations",
                column: "CreatorId");

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
            var sql = System.IO.File.ReadAllText("Migrations/orleans_down.sql");
            migrationBuilder.Sql(sql);

            migrationBuilder.DropTable(
                name: "ChannelEntitlementOverwrites");

            migrationBuilder.DropTable(
                name: "ServerMemberArchetypes");

            migrationBuilder.DropTable(
                name: "UserAgreements");

            migrationBuilder.DropTable(
                name: "Channels");

            migrationBuilder.DropTable(
                name: "Archetypes");

            migrationBuilder.DropTable(
                name: "UsersToServerRelations");

            migrationBuilder.DropTable(
                name: "Servers");

            migrationBuilder.DropTable(
                name: "Users");
        }
    }
}
