using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

#pragma warning disable CA1814 // Prefer jagged arrays over multidimensional

namespace Argon.Api.Migrations
{
    /// <inheritdoc />
    public partial class Initial : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.CreateTable(
                name: "ArgonMessageReactions",
                columns: table => new
                {
                    ServerId = table.Column<Guid>(type: "uuid", nullable: false),
                    ChannelId = table.Column<Guid>(type: "uuid", nullable: false),
                    MessageId = table.Column<decimal>(type: "numeric(20,0)", nullable: false),
                    UserId = table.Column<Guid>(type: "uuid", nullable: false),
                    Reaction = table.Column<string>(type: "character varying(32)", maxLength: 32, nullable: false),
                    Timestamp = table.Column<long>(type: "bigint", nullable: false),
                    Id = table.Column<Guid>(type: "uuid", nullable: false),
                    CreatedAt = table.Column<long>(type: "bigint", nullable: false),
                    UpdatedAt = table.Column<long>(type: "bigint", nullable: false),
                    DeletedAt = table.Column<long>(type: "bigint", nullable: true),
                    IsDeleted = table.Column<bool>(type: "boolean", nullable: false),
                    CreatorId = table.Column<Guid>(type: "uuid", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_ArgonMessageReactions", x => new { x.ServerId, x.ChannelId, x.MessageId, x.UserId, x.Reaction });
                });

            migrationBuilder.CreateTable(
                name: "ArgonMessages_Counters",
                columns: table => new
                {
                    ServerId = table.Column<Guid>(type: "uuid", nullable: false),
                    ChannelId = table.Column<Guid>(type: "uuid", nullable: false),
                    NextMessageId = table.Column<decimal>(type: "numeric(20,0)", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_ArgonMessages_Counters", x => new { x.ChannelId, x.ServerId });
                });

            migrationBuilder.CreateTable(
                name: "MeetInviteLinks",
                columns: table => new
                {
                    Id = table.Column<decimal>(type: "numeric(20,0)", nullable: false),
                    AssociatedChannelId = table.Column<Guid>(type: "uuid", nullable: true),
                    AssociatedServerId = table.Column<Guid>(type: "uuid", nullable: true),
                    NoChannelSharedKey = table.Column<Guid>(type: "uuid", nullable: true),
                    ExpireDate = table.Column<long>(type: "bigint", nullable: false),
                    CreatedAt = table.Column<long>(type: "bigint", nullable: false),
                    UpdatedAt = table.Column<long>(type: "bigint", nullable: false),
                    DeletedAt = table.Column<long>(type: "bigint", nullable: true),
                    IsDeleted = table.Column<bool>(type: "boolean", nullable: false),
                    CreatorId = table.Column<Guid>(type: "uuid", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_MeetInviteLinks", x => x.Id);
                });

            migrationBuilder.CreateTable(
                name: "Messages",
                columns: table => new
                {
                    MessageId = table.Column<decimal>(type: "numeric(20,0)", nullable: false),
                    ServerId = table.Column<Guid>(type: "uuid", nullable: false),
                    ChannelId = table.Column<Guid>(type: "uuid", nullable: false),
                    Reply = table.Column<decimal>(type: "numeric(20,0)", nullable: true),
                    Text = table.Column<string>(type: "character varying(2048)", maxLength: 2048, nullable: false),
                    Entities = table.Column<string>(type: "jsonb", nullable: false),
                    CreatedAt = table.Column<long>(type: "bigint", nullable: false),
                    UpdatedAt = table.Column<long>(type: "bigint", nullable: false),
                    DeletedAt = table.Column<long>(type: "bigint", nullable: true),
                    IsDeleted = table.Column<bool>(type: "boolean", nullable: false),
                    CreatorId = table.Column<Guid>(type: "uuid", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_Messages", x => new { x.ServerId, x.ChannelId, x.MessageId });
                });

            migrationBuilder.CreateTable(
                name: "Reservation",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "uuid", nullable: false),
                    UserName = table.Column<string>(type: "text", nullable: false),
                    NormalizedUserName = table.Column<string>(type: "text", nullable: false),
                    IsBanned = table.Column<bool>(type: "boolean", nullable: false),
                    IsReserved = table.Column<bool>(type: "boolean", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_Reservation", x => x.Id);
                });

            migrationBuilder.CreateTable(
                name: "Servers",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "uuid", nullable: false),
                    Name = table.Column<string>(type: "character varying(64)", maxLength: 64, nullable: false),
                    Description = table.Column<string>(type: "character varying(1024)", maxLength: 1024, nullable: true),
                    AvatarFileId = table.Column<string>(type: "character varying(128)", maxLength: 128, nullable: true),
                    TopBannedFileId = table.Column<string>(type: "character varying(128)", maxLength: 128, nullable: true),
                    CreatedAt = table.Column<long>(type: "bigint", nullable: false),
                    UpdatedAt = table.Column<long>(type: "bigint", nullable: false),
                    DeletedAt = table.Column<long>(type: "bigint", nullable: true),
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
                    NormalizedUsername = table.Column<string>(type: "character varying(64)", maxLength: 64, nullable: false),
                    DisplayName = table.Column<string>(type: "character varying(64)", maxLength: 64, nullable: false),
                    PhoneNumber = table.Column<string>(type: "character varying(64)", maxLength: 64, nullable: true),
                    PasswordDigest = table.Column<string>(type: "character varying(512)", maxLength: 512, nullable: true),
                    AvatarFileId = table.Column<string>(type: "character varying(128)", maxLength: 128, nullable: true),
                    OtpHash = table.Column<string>(type: "character varying(128)", maxLength: 128, nullable: true),
                    LockdownReason = table.Column<int>(type: "integer", nullable: false),
                    LockDownExpiration = table.Column<long>(type: "bigint", nullable: true),
                    CreatedAt = table.Column<long>(type: "bigint", nullable: false),
                    UpdatedAt = table.Column<long>(type: "bigint", nullable: false),
                    DeletedAt = table.Column<long>(type: "bigint", nullable: true),
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
                    IsGroup = table.Column<bool>(type: "boolean", nullable: false),
                    IsDefault = table.Column<bool>(type: "boolean", nullable: false),
                    Colour = table.Column<int>(type: "integer", nullable: false),
                    IconFileId = table.Column<string>(type: "character varying(128)", maxLength: 128, nullable: true),
                    CreatedAt = table.Column<long>(type: "bigint", nullable: false),
                    UpdatedAt = table.Column<long>(type: "bigint", nullable: false),
                    DeletedAt = table.Column<long>(type: "bigint", nullable: true),
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
                    CreatedAt = table.Column<long>(type: "bigint", nullable: false),
                    UpdatedAt = table.Column<long>(type: "bigint", nullable: false),
                    DeletedAt = table.Column<long>(type: "bigint", nullable: true),
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
                name: "ServerInvites",
                columns: table => new
                {
                    Id = table.Column<decimal>(type: "numeric(20,0)", nullable: false),
                    Expired = table.Column<long>(type: "bigint", nullable: false),
                    ServerId = table.Column<Guid>(type: "uuid", nullable: false),
                    CreatedAt = table.Column<long>(type: "bigint", nullable: false),
                    UpdatedAt = table.Column<long>(type: "bigint", nullable: false),
                    DeletedAt = table.Column<long>(type: "bigint", nullable: true),
                    IsDeleted = table.Column<bool>(type: "boolean", nullable: false),
                    CreatorId = table.Column<Guid>(type: "uuid", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_ServerInvites", x => x.Id);
                    table.ForeignKey(
                        name: "FK_ServerInvites_Servers_ServerId",
                        column: x => x.ServerId,
                        principalTable: "Servers",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Cascade);
                });

            migrationBuilder.CreateTable(
                name: "SocialIntegrations",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "uuid", nullable: false),
                    UserId = table.Column<Guid>(type: "uuid", nullable: false),
                    SocialId = table.Column<string>(type: "character varying(128)", maxLength: 128, nullable: false),
                    Kind = table.Column<int>(type: "integer", nullable: false),
                    UserData = table.Column<string>(type: "jsonb", nullable: false),
                    CreatedAt = table.Column<long>(type: "bigint", nullable: false),
                    UpdatedAt = table.Column<long>(type: "bigint", nullable: false),
                    DeletedAt = table.Column<long>(type: "bigint", nullable: true),
                    IsDeleted = table.Column<bool>(type: "boolean", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_SocialIntegrations", x => x.Id);
                    table.ForeignKey(
                        name: "FK_SocialIntegrations_Users_UserId",
                        column: x => x.UserId,
                        principalTable: "Users",
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
                    CreatedAt = table.Column<long>(type: "bigint", nullable: false),
                    UpdatedAt = table.Column<long>(type: "bigint", nullable: false),
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
                name: "UserProfiles",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "uuid", nullable: false),
                    UserId = table.Column<Guid>(type: "uuid", nullable: false),
                    CustomStatus = table.Column<string>(type: "character varying(128)", maxLength: 128, nullable: true),
                    CustomStatusIconId = table.Column<string>(type: "character varying(128)", maxLength: 128, nullable: true),
                    BannerFileId = table.Column<string>(type: "character varying(128)", maxLength: 128, nullable: true),
                    DateOfBirth = table.Column<DateOnly>(type: "date", nullable: true),
                    Bio = table.Column<string>(type: "character varying(512)", maxLength: 512, nullable: true),
                    IsPremium = table.Column<bool>(type: "boolean", nullable: false),
                    Badges = table.Column<string>(type: "jsonb", nullable: false),
                    CreatedAt = table.Column<long>(type: "bigint", nullable: false),
                    UpdatedAt = table.Column<long>(type: "bigint", nullable: false),
                    DeletedAt = table.Column<long>(type: "bigint", nullable: true),
                    IsDeleted = table.Column<bool>(type: "boolean", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_UserProfiles", x => x.Id);
                    table.ForeignKey(
                        name: "FK_UserProfiles_Users_UserId",
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
                    JoinedAt = table.Column<long>(type: "bigint", nullable: false),
                    CreatedAt = table.Column<long>(type: "bigint", nullable: false),
                    UpdatedAt = table.Column<long>(type: "bigint", nullable: false),
                    DeletedAt = table.Column<long>(type: "bigint", nullable: true),
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
                    CreatedAt = table.Column<long>(type: "bigint", nullable: false),
                    UpdatedAt = table.Column<long>(type: "bigint", nullable: false),
                    DeletedAt = table.Column<long>(type: "bigint", nullable: true),
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
                columns: new[] { "Id", "AvatarFileId", "CreatedAt", "CreatorId", "DeletedAt", "Description", "IsDeleted", "Name", "TopBannedFileId", "UpdatedAt" },
                values: new object[] { new Guid("11111111-0000-1111-1111-111111111111"), "", -62135596800000L, new Guid("11111111-2222-1111-2222-111111111111"), null, "", false, "system_server", null, -62135596800000L });

            migrationBuilder.InsertData(
                table: "Users",
                columns: new[] { "Id", "AvatarFileId", "CreatedAt", "DeletedAt", "DisplayName", "Email", "IsDeleted", "LockDownExpiration", "LockdownReason", "NormalizedUsername", "OtpHash", "PasswordDigest", "PhoneNumber", "UpdatedAt", "Username" },
                values: new object[] { new Guid("11111111-2222-1111-2222-111111111111"), null, -62135596800000L, null, "System", "system@argon.gl", false, null, 0, "system", null, null, null, -62135596800000L, "system" });

            migrationBuilder.InsertData(
                table: "Archetypes",
                columns: new[] { "Id", "Colour", "CreatedAt", "CreatorId", "DeletedAt", "Description", "Entitlement", "IconFileId", "IsDefault", "IsDeleted", "IsGroup", "IsHidden", "IsLocked", "IsMentionable", "Name", "ServerId", "UpdatedAt" },
                values: new object[,]
                {
                    { new Guid("11111111-3333-0000-1111-111111111111"), -8355712, 1732377674205L, new Guid("11111111-2222-1111-2222-111111111111"), null, "Default role for everyone in this server", 15760355m, null, false, false, false, false, false, true, "everyone", new Guid("11111111-0000-1111-1111-111111111111"), -62135596800000L },
                    { new Guid("11111111-4444-0000-1111-111111111111"), -8355712, 1732377674205L, new Guid("11111111-2222-1111-2222-111111111111"), null, "Default role for owner in this server", -1m, null, false, false, false, true, true, false, "owner", new Guid("11111111-0000-1111-1111-111111111111"), -62135596800000L }
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
                name: "IX_ArgonMessageReactions_CreatorId",
                table: "ArgonMessageReactions",
                column: "CreatorId");

            migrationBuilder.CreateIndex(
                name: "IX_ArgonMessageReactions_ServerId_ChannelId_MessageId",
                table: "ArgonMessageReactions",
                columns: new[] { "ServerId", "ChannelId", "MessageId" },
                unique: true);

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
                name: "IX_MeetInviteLinks_CreatorId",
                table: "MeetInviteLinks",
                column: "CreatorId");

            migrationBuilder.CreateIndex(
                name: "IX_Messages_CreatorId",
                table: "Messages",
                column: "CreatorId");

            migrationBuilder.CreateIndex(
                name: "IX_Messages_ServerId_ChannelId_MessageId",
                table: "Messages",
                columns: new[] { "ServerId", "ChannelId", "MessageId" },
                unique: true);

            migrationBuilder.CreateIndex(
                name: "IX_Reservation_NormalizedUserName",
                table: "Reservation",
                column: "NormalizedUserName",
                unique: true);

            migrationBuilder.CreateIndex(
                name: "IX_ServerInvites_CreatorId",
                table: "ServerInvites",
                column: "CreatorId");

            migrationBuilder.CreateIndex(
                name: "IX_ServerInvites_ServerId",
                table: "ServerInvites",
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
                name: "IX_SocialIntegrations_SocialId",
                table: "SocialIntegrations",
                column: "SocialId");

            migrationBuilder.CreateIndex(
                name: "IX_SocialIntegrations_UserId",
                table: "SocialIntegrations",
                column: "UserId");

            migrationBuilder.CreateIndex(
                name: "IX_UserAgreements_UserId",
                table: "UserAgreements",
                column: "UserId");

            migrationBuilder.CreateIndex(
                name: "IX_UserProfiles_UserId",
                table: "UserProfiles",
                column: "UserId",
                unique: true);

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
            migrationBuilder.DropTable(
                name: "ArgonMessageReactions");

            migrationBuilder.DropTable(
                name: "ArgonMessages_Counters");

            migrationBuilder.DropTable(
                name: "ChannelEntitlementOverwrites");

            migrationBuilder.DropTable(
                name: "MeetInviteLinks");

            migrationBuilder.DropTable(
                name: "Messages");

            migrationBuilder.DropTable(
                name: "Reservation");

            migrationBuilder.DropTable(
                name: "ServerInvites");

            migrationBuilder.DropTable(
                name: "ServerMemberArchetypes");

            migrationBuilder.DropTable(
                name: "SocialIntegrations");

            migrationBuilder.DropTable(
                name: "UserAgreements");

            migrationBuilder.DropTable(
                name: "UserProfiles");

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
