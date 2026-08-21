using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

#pragma warning disable CA1814 // Prefer jagged arrays over multidimensional

namespace Argon.Core.Migrations
{
    /// <inheritdoc />
    public partial class Initial : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.CreateTable(
                name: "Messages",
                columns: table => new
                {
                    MessageId = table.Column<long>(type: "BIGINT", nullable: false, defaultValueSql: "unique_rowid()"),
                    SpaceId = table.Column<Guid>(type: "uuid", nullable: false),
                    ChannelId = table.Column<Guid>(type: "uuid", nullable: false),
                    Reply = table.Column<long>(type: "BIGINT", nullable: true),
                    Text = table.Column<string>(type: "text", nullable: false),
                    Entities = table.Column<string>(type: "jsonb", nullable: false),
                    CreatedAt = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false),
                    UpdatedAt = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false),
                    DeletedAt = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: true),
                    IsDeleted = table.Column<bool>(type: "boolean", nullable: false),
                    CreatorId = table.Column<Guid>(type: "uuid", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_Messages", x => new { x.SpaceId, x.ChannelId, x.MessageId });
                });

            migrationBuilder.CreateTable(
                name: "Reservation",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "uuid", nullable: false),
                    UserName = table.Column<string>(type: "text", nullable: false),
                    NormalizedUserName = table.Column<string>(type: "varchar(64)", maxLength: 64, nullable: false),
                    IsBanned = table.Column<bool>(type: "boolean", nullable: false),
                    IsReserved = table.Column<bool>(type: "boolean", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_Reservation", x => x.Id);
                });

            migrationBuilder.CreateTable(
                name: "Spaces",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "uuid", nullable: false),
                    Name = table.Column<string>(type: "text", maxLength: 64, nullable: false),
                    Description = table.Column<string>(type: "text", maxLength: 1024, nullable: true),
                    AvatarFileId = table.Column<string>(type: "text", maxLength: 128, nullable: true),
                    TopBannedFileId = table.Column<string>(type: "text", maxLength: 128, nullable: true),
                    CreatedAt = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false),
                    UpdatedAt = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false),
                    DeletedAt = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: true),
                    IsDeleted = table.Column<bool>(type: "boolean", nullable: false),
                    CreatorId = table.Column<Guid>(type: "uuid", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_Spaces", x => x.Id);
                });

            migrationBuilder.CreateTable(
                name: "Users",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "uuid", nullable: false),
                    Email = table.Column<string>(type: "text", maxLength: 255, nullable: false),
                    Username = table.Column<string>(type: "text", maxLength: 64, nullable: false),
                    DisplayName = table.Column<string>(type: "text", nullable: false),
                    PhoneNumber = table.Column<string>(type: "text", nullable: true),
                    PasswordDigest = table.Column<string>(type: "text", nullable: true),
                    AvatarFileId = table.Column<string>(type: "text", nullable: true),
                    DateOfBirth = table.Column<DateOnly>(type: "date", nullable: true),
                    TotpSecret = table.Column<string>(type: "text", maxLength: 512, nullable: true),
                    PreferredAuthMode = table.Column<int>(type: "integer", nullable: false),
                    PreferredOtpMethod = table.Column<int>(type: "integer", nullable: false),
                    NormalizedEmail = table.Column<string>(type: "varchar(255)", nullable: false, computedColumnSql: "lower(\"Email\")", stored: true),
                    NormalizedUsername = table.Column<string>(type: "varchar(64)", nullable: false, computedColumnSql: "lower(\"Username\")", stored: true),
                    AllowedSendOptionalEmails = table.Column<bool>(type: "boolean", nullable: false),
                    AgreeTOS = table.Column<bool>(type: "boolean", nullable: false),
                    LockdownReason = table.Column<int>(type: "integer", nullable: false),
                    LockDownExpiration = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: true),
                    LockDownIsAppealable = table.Column<bool>(type: "boolean", nullable: false),
                    CreatedAt = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false),
                    UpdatedAt = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false),
                    DeletedAt = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: true),
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
                    SpaceId = table.Column<Guid>(type: "uuid", nullable: false),
                    Name = table.Column<string>(type: "text", nullable: false),
                    Description = table.Column<string>(type: "text", nullable: false),
                    Entitlement = table.Column<long>(type: "BIGINT", nullable: false),
                    IsMentionable = table.Column<bool>(type: "boolean", nullable: false),
                    IsLocked = table.Column<bool>(type: "boolean", nullable: false),
                    IsHidden = table.Column<bool>(type: "boolean", nullable: false),
                    IsGroup = table.Column<bool>(type: "boolean", nullable: false),
                    IsDefault = table.Column<bool>(type: "boolean", nullable: false),
                    Colour = table.Column<int>(type: "integer", nullable: false),
                    IconFileId = table.Column<string>(type: "text", nullable: true),
                    CreatedAt = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false),
                    UpdatedAt = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false),
                    DeletedAt = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: true),
                    IsDeleted = table.Column<bool>(type: "boolean", nullable: false),
                    CreatorId = table.Column<Guid>(type: "uuid", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_Archetypes", x => x.Id);
                    table.ForeignKey(
                        name: "FK_Archetypes_Spaces_SpaceId",
                        column: x => x.SpaceId,
                        principalTable: "Spaces",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Cascade);
                });

            migrationBuilder.CreateTable(
                name: "Channels",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "uuid", nullable: false),
                    ChannelType = table.Column<int>(type: "integer", nullable: false),
                    SpaceId = table.Column<Guid>(type: "uuid", nullable: false),
                    Name = table.Column<string>(type: "text", maxLength: 128, nullable: false),
                    Description = table.Column<string>(type: "text", maxLength: 1024, nullable: true),
                    SlowMode = table.Column<TimeSpan>(type: "interval", nullable: true),
                    DoNotRestrictBoosters = table.Column<bool>(type: "boolean", nullable: false),
                    CreatedAt = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false),
                    UpdatedAt = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false),
                    DeletedAt = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: true),
                    IsDeleted = table.Column<bool>(type: "boolean", nullable: false),
                    CreatorId = table.Column<Guid>(type: "uuid", nullable: false),
                    FractionalIndex = table.Column<string>(type: "text", maxLength: 64, nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_Channels", x => x.Id);
                    table.ForeignKey(
                        name: "FK_Channels_Spaces_SpaceId",
                        column: x => x.SpaceId,
                        principalTable: "Spaces",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Cascade);
                });

            migrationBuilder.CreateTable(
                name: "Invites",
                columns: table => new
                {
                    Id = table.Column<long>(type: "bigint", nullable: false),
                    ExpireAt = table.Column<DateTimeOffset>(type: "TIMESTAMPTZ", nullable: false),
                    SpaceId = table.Column<Guid>(type: "uuid", nullable: false),
                    CreatedAt = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false),
                    UpdatedAt = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false),
                    DeletedAt = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: true),
                    IsDeleted = table.Column<bool>(type: "boolean", nullable: false),
                    CreatorId = table.Column<Guid>(type: "uuid", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_Invites", x => x.Id);
                    table.ForeignKey(
                        name: "FK_Invites_Spaces_SpaceId",
                        column: x => x.SpaceId,
                        principalTable: "Spaces",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Cascade);
                });

            migrationBuilder.CreateTable(
                name: "DeviceHistories",
                columns: table => new
                {
                    MachineId = table.Column<string>(type: "text", maxLength: 64, nullable: false),
                    UserId = table.Column<Guid>(type: "uuid", nullable: false),
                    LastLoginTime = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: true),
                    LastKnownIP = table.Column<string>(type: "text", maxLength: 64, nullable: false),
                    RegionAddress = table.Column<string>(type: "text", maxLength: 64, nullable: false),
                    AppId = table.Column<string>(type: "text", maxLength: 64, nullable: false),
                    DeviceType = table.Column<int>(type: "integer", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_DeviceHistories", x => new { x.UserId, x.MachineId });
                    table.ForeignKey(
                        name: "FK_DeviceHistories_Users_UserId",
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
                    CustomStatus = table.Column<string>(type: "text", maxLength: 128, nullable: true),
                    CustomStatusIconId = table.Column<string>(type: "text", maxLength: 128, nullable: true),
                    BannerFileId = table.Column<string>(type: "text", maxLength: 128, nullable: true),
                    DateOfBirth = table.Column<DateOnly>(type: "date", nullable: true),
                    Bio = table.Column<string>(type: "text", maxLength: 512, nullable: true),
                    Badges = table.Column<string>(type: "jsonb", nullable: false),
                    CreatedAt = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false),
                    UpdatedAt = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false),
                    DeletedAt = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: true),
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
                    SpaceId = table.Column<Guid>(type: "uuid", nullable: false),
                    UserId = table.Column<Guid>(type: "uuid", nullable: false),
                    JoinedAt = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false),
                    CreatedAt = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false),
                    UpdatedAt = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false),
                    DeletedAt = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: true),
                    IsDeleted = table.Column<bool>(type: "boolean", nullable: false),
                    CreatorId = table.Column<Guid>(type: "uuid", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_UsersToServerRelations", x => x.Id);
                    table.ForeignKey(
                        name: "FK_UsersToServerRelations_Spaces_SpaceId",
                        column: x => x.SpaceId,
                        principalTable: "Spaces",
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
                    SpaceMemberId = table.Column<Guid>(type: "uuid", nullable: true),
                    Allow = table.Column<decimal>(type: "numeric(20,0)", nullable: false),
                    Deny = table.Column<decimal>(type: "numeric(20,0)", nullable: false),
                    CreatedAt = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false),
                    UpdatedAt = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false),
                    DeletedAt = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: true),
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
                        name: "FK_ChannelEntitlementOverwrites_UsersToServerRelations_SpaceMe~",
                        column: x => x.SpaceMemberId,
                        principalTable: "UsersToServerRelations",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Restrict);
                });

            migrationBuilder.CreateTable(
                name: "MemberArchetypes",
                columns: table => new
                {
                    SpaceMemberId = table.Column<Guid>(type: "uuid", nullable: false),
                    ArchetypeId = table.Column<Guid>(type: "uuid", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_MemberArchetypes", x => new { x.SpaceMemberId, x.ArchetypeId });
                    table.ForeignKey(
                        name: "FK_MemberArchetypes_Archetypes_ArchetypeId",
                        column: x => x.ArchetypeId,
                        principalTable: "Archetypes",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Cascade);
                    table.ForeignKey(
                        name: "FK_MemberArchetypes_UsersToServerRelations_SpaceMemberId",
                        column: x => x.SpaceMemberId,
                        principalTable: "UsersToServerRelations",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Cascade);
                });

            migrationBuilder.CreateTable(
                name: "CouponRedemption",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "uuid", nullable: false),
                    CouponId = table.Column<Guid>(type: "uuid", nullable: false),
                    UserId = table.Column<Guid>(type: "uuid", nullable: false),
                    RedeemedAt = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_CouponRedemption", x => x.Id);
                });

            migrationBuilder.CreateTable(
                name: "Coupons",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "uuid", nullable: false),
                    Code = table.Column<string>(type: "varchar(64)", maxLength: 64, nullable: false),
                    Description = table.Column<string>(type: "text", maxLength: 512, nullable: true),
                    ValidFrom = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false),
                    ValidTo = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false),
                    MaxRedemptions = table.Column<int>(type: "integer", nullable: false),
                    RedemptionCount = table.Column<int>(type: "integer", nullable: false),
                    IsActive = table.Column<bool>(type: "boolean", nullable: false),
                    ReferenceItemEntityId = table.Column<Guid>(type: "uuid", nullable: true),
                    CreatedAt = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false),
                    UpdatedAt = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false),
                    DeletedAt = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: true),
                    IsDeleted = table.Column<bool>(type: "boolean", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_Coupons", x => x.Id);
                });

            migrationBuilder.CreateTable(
                name: "ItemUseScenario",
                columns: table => new
                {
                    Key = table.Column<Guid>(type: "uuid", nullable: false),
                    ScenarioType = table.Column<string>(type: "text", maxLength: 21, nullable: false),
                    Edition = table.Column<string>(type: "text", nullable: true),
                    PlanId = table.Column<string>(type: "text", nullable: true),
                    ReferenceItemId = table.Column<Guid>(type: "uuid", nullable: true),
                    Code = table.Column<string>(type: "text", nullable: true),
                    ServiceKey = table.Column<string>(type: "text", nullable: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_ItemUseScenario", x => x.Key);
                });

            migrationBuilder.CreateTable(
                name: "Items",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "uuid", nullable: false),
                    TemplateId = table.Column<string>(type: "varchar(255)", maxLength: 255, nullable: false),
                    IsUsable = table.Column<bool>(type: "boolean", nullable: false),
                    IsGiftable = table.Column<bool>(type: "boolean", nullable: false),
                    UseVector = table.Column<int>(type: "integer", nullable: true),
                    ReceivedFrom = table.Column<Guid>(type: "uuid", nullable: true),
                    OwnerId = table.Column<Guid>(type: "uuid", nullable: false),
                    IsAffectBadge = table.Column<bool>(type: "boolean", nullable: false),
                    TTL = table.Column<TimeSpan>(type: "interval", nullable: true),
                    RedemptionId = table.Column<Guid>(type: "uuid", nullable: true),
                    IsReference = table.Column<bool>(type: "boolean", nullable: false),
                    ScenarioKey = table.Column<Guid>(type: "uuid", nullable: true),
                    CreatedAt = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false),
                    UpdatedAt = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false),
                    DeletedAt = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: true),
                    IsDeleted = table.Column<bool>(type: "boolean", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_Items", x => x.Id);
                    table.ForeignKey(
                        name: "FK_Items_CouponRedemption_RedemptionId",
                        column: x => x.RedemptionId,
                        principalTable: "CouponRedemption",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Cascade);
                    table.ForeignKey(
                        name: "FK_Items_ItemUseScenario_ScenarioKey",
                        column: x => x.ScenarioKey,
                        principalTable: "ItemUseScenario",
                        principalColumn: "Key",
                        onDelete: ReferentialAction.SetNull);
                });

            migrationBuilder.CreateTable(
                name: "UnreadInventoryItems",
                columns: table => new
                {
                    OwnerUserId = table.Column<Guid>(type: "uuid", nullable: false),
                    InventoryItemId = table.Column<Guid>(type: "uuid", nullable: false),
                    CreatedAt = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false),
                    TemplateId = table.Column<string>(type: "varchar(255)", maxLength: 255, nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_UnreadInventoryItems", x => new { x.OwnerUserId, x.InventoryItemId });
                    table.ForeignKey(
                        name: "FK_UnreadInventoryItems_Items_InventoryItemId",
                        column: x => x.InventoryItemId,
                        principalTable: "Items",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Cascade);
                });

            migrationBuilder.InsertData(
                table: "Spaces",
                columns: new[] { "Id", "AvatarFileId", "CreatedAt", "CreatorId", "DeletedAt", "Description", "IsDeleted", "Name", "TopBannedFileId", "UpdatedAt" },
                values: new object[] { new Guid("11111111-0000-1111-1111-111111111111"), "", new DateTimeOffset(new DateTime(1, 1, 1, 0, 0, 0, 0, DateTimeKind.Unspecified), new TimeSpan(0, 0, 0, 0, 0)), new Guid("11111111-2222-1111-2222-111111111111"), null, "", false, "system_server", null, new DateTimeOffset(new DateTime(1, 1, 1, 0, 0, 0, 0, DateTimeKind.Unspecified), new TimeSpan(0, 0, 0, 0, 0)) });

            migrationBuilder.InsertData(
                table: "Users",
                columns: new[] { "Id", "AgreeTOS", "AllowedSendOptionalEmails", "AvatarFileId", "CreatedAt", "DateOfBirth", "DeletedAt", "DisplayName", "Email", "IsDeleted", "LockDownExpiration", "LockDownIsAppealable", "LockdownReason", "PasswordDigest", "PhoneNumber", "PreferredAuthMode", "PreferredOtpMethod", "TotpSecret", "UpdatedAt", "Username" },
                values: new object[] { new Guid("11111111-2222-1111-2222-111111111111"), false, false, null, new DateTimeOffset(new DateTime(1, 1, 1, 0, 0, 0, 0, DateTimeKind.Unspecified), new TimeSpan(0, 0, 0, 0, 0)), null, null, "System", "system@argon.gl", false, null, false, 0, null, null, 0, 0, null, new DateTimeOffset(new DateTime(1, 1, 1, 0, 0, 0, 0, DateTimeKind.Unspecified), new TimeSpan(0, 0, 0, 0, 0)), "system" });

            migrationBuilder.InsertData(
                table: "Archetypes",
                columns: new[] { "Id", "Colour", "CreatedAt", "CreatorId", "DeletedAt", "Description", "Entitlement", "IconFileId", "IsDefault", "IsDeleted", "IsGroup", "IsHidden", "IsLocked", "IsMentionable", "Name", "SpaceId", "UpdatedAt" },
                values: new object[,]
                {
                    { new Guid("11111111-3333-0000-1111-111111111111"), -8355712, new DateTimeOffset(new DateTime(2025, 1, 1, 0, 0, 0, 0, DateTimeKind.Unspecified), new TimeSpan(0, 0, 0, 0, 0)), new Guid("11111111-2222-1111-2222-111111111111"), null, "Default role for everyone in this space", 15761383L, null, false, false, false, false, false, true, "everyone", new Guid("11111111-0000-1111-1111-111111111111"), new DateTimeOffset(new DateTime(1, 1, 1, 0, 0, 0, 0, DateTimeKind.Unspecified), new TimeSpan(0, 0, 0, 0, 0)) },
                    { new Guid("11111111-4444-0000-1111-111111111111"), -8355712, new DateTimeOffset(new DateTime(2025, 1, 1, 0, 0, 0, 0, DateTimeKind.Unspecified), new TimeSpan(0, 0, 0, 0, 0)), new Guid("11111111-2222-1111-2222-111111111111"), null, "Default role for owner in this space", -1L, null, false, false, false, true, true, false, "owner", new Guid("11111111-0000-1111-1111-111111111111"), new DateTimeOffset(new DateTime(1, 1, 1, 0, 0, 0, 0, DateTimeKind.Unspecified), new TimeSpan(0, 0, 0, 0, 0)) }
                });

            migrationBuilder.CreateIndex(
                name: "IX_Archetypes_CreatorId",
                table: "Archetypes",
                column: "CreatorId");

            migrationBuilder.CreateIndex(
                name: "IX_Archetypes_SpaceId",
                table: "Archetypes",
                column: "SpaceId");

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
                name: "IX_ChannelEntitlementOverwrites_SpaceMemberId",
                table: "ChannelEntitlementOverwrites",
                column: "SpaceMemberId");

            migrationBuilder.CreateIndex(
                name: "IX_Channels_CreatorId",
                table: "Channels",
                column: "CreatorId");

            migrationBuilder.CreateIndex(
                name: "IX_Channels_SpaceId",
                table: "Channels",
                column: "SpaceId");

            migrationBuilder.CreateIndex(
                name: "IX_CouponRedemption_CouponId",
                table: "CouponRedemption",
                column: "CouponId");

            migrationBuilder.CreateIndex(
                name: "IX_Coupons_Code",
                table: "Coupons",
                column: "Code",
                unique: true);

            migrationBuilder.CreateIndex(
                name: "IX_Coupons_ReferenceItemEntityId",
                table: "Coupons",
                column: "ReferenceItemEntityId");

            migrationBuilder.CreateIndex(
                name: "IX_Invites_CreatorId",
                table: "Invites",
                column: "CreatorId");

            migrationBuilder.CreateIndex(
                name: "IX_Invites_SpaceId",
                table: "Invites",
                column: "SpaceId");

            migrationBuilder.CreateIndex(
                name: "IX_ItemUseScenario_ReferenceItemId",
                table: "ItemUseScenario",
                column: "ReferenceItemId");

            migrationBuilder.CreateIndex(
                name: "IX_Items_Id_IsReference",
                table: "Items",
                columns: new[] { "Id", "IsReference" });

            migrationBuilder.CreateIndex(
                name: "IX_Items_Id_OwnerId",
                table: "Items",
                columns: new[] { "Id", "OwnerId" });

            migrationBuilder.CreateIndex(
                name: "IX_Items_IsReference",
                table: "Items",
                column: "IsReference");

            migrationBuilder.CreateIndex(
                name: "IX_Items_OwnerId",
                table: "Items",
                column: "OwnerId");

            migrationBuilder.CreateIndex(
                name: "IX_Items_OwnerId_IsAffectBadge",
                table: "Items",
                columns: new[] { "OwnerId", "IsAffectBadge" });

            migrationBuilder.CreateIndex(
                name: "IX_Items_OwnerId_TemplateId",
                table: "Items",
                columns: new[] { "OwnerId", "TemplateId" });

            migrationBuilder.CreateIndex(
                name: "IX_Items_RedemptionId",
                table: "Items",
                column: "RedemptionId");

            migrationBuilder.CreateIndex(
                name: "IX_Items_ScenarioKey",
                table: "Items",
                column: "ScenarioKey");

            migrationBuilder.CreateIndex(
                name: "IX_Items_TemplateId",
                table: "Items",
                column: "TemplateId");

            migrationBuilder.CreateIndex(
                name: "IX_MemberArchetypes_ArchetypeId",
                table: "MemberArchetypes",
                column: "ArchetypeId");

            migrationBuilder.CreateIndex(
                name: "IX_Messages_CreatorId",
                table: "Messages",
                column: "CreatorId");

            migrationBuilder.CreateIndex(
                name: "IX_Messages_SpaceId_ChannelId_CreatedAt",
                table: "Messages",
                columns: new[] { "SpaceId", "ChannelId", "CreatedAt" })
                .Annotation("Npgsql:IndexInclude", new[] { "Text", "Entities" });

            migrationBuilder.CreateIndex(
                name: "IX_Reservation_NormalizedUserName",
                table: "Reservation",
                column: "NormalizedUserName",
                unique: true);

            migrationBuilder.CreateIndex(
                name: "IX_Spaces_CreatorId",
                table: "Spaces",
                column: "CreatorId");

            migrationBuilder.CreateIndex(
                name: "IX_UnreadInventoryItems_InventoryItemId",
                table: "UnreadInventoryItems",
                column: "InventoryItemId");

            migrationBuilder.CreateIndex(
                name: "IX_UnreadInventoryItems_TemplateId",
                table: "UnreadInventoryItems",
                column: "TemplateId");

            migrationBuilder.CreateIndex(
                name: "ix_unread_owner_created",
                table: "UnreadInventoryItems",
                columns: new[] { "OwnerUserId", "CreatedAt" });

            migrationBuilder.CreateIndex(
                name: "IX_UserProfiles_UserId",
                table: "UserProfiles",
                column: "UserId",
                unique: true);

            migrationBuilder.CreateIndex(
                name: "IX_Users_NormalizedEmail",
                table: "Users",
                column: "NormalizedEmail",
                unique: true);

            migrationBuilder.CreateIndex(
                name: "IX_Users_NormalizedUsername",
                table: "Users",
                column: "NormalizedUsername",
                unique: true);

            migrationBuilder.CreateIndex(
                name: "IX_UsersToServerRelations_CreatorId",
                table: "UsersToServerRelations",
                column: "CreatorId");

            migrationBuilder.CreateIndex(
                name: "IX_UsersToServerRelations_SpaceId",
                table: "UsersToServerRelations",
                column: "SpaceId");

            migrationBuilder.CreateIndex(
                name: "IX_UsersToServerRelations_UserId",
                table: "UsersToServerRelations",
                column: "UserId");

            migrationBuilder.AddForeignKey(
                name: "FK_CouponRedemption_Coupons_CouponId",
                table: "CouponRedemption",
                column: "CouponId",
                principalTable: "Coupons",
                principalColumn: "Id",
                onDelete: ReferentialAction.Cascade);

            migrationBuilder.AddForeignKey(
                name: "FK_Coupons_Items_ReferenceItemEntityId",
                table: "Coupons",
                column: "ReferenceItemEntityId",
                principalTable: "Items",
                principalColumn: "Id",
                onDelete: ReferentialAction.SetNull);

            migrationBuilder.AddForeignKey(
                name: "FK_ItemUseScenario_Items_ReferenceItemId",
                table: "ItemUseScenario",
                column: "ReferenceItemId",
                principalTable: "Items",
                principalColumn: "Id",
                onDelete: ReferentialAction.Cascade);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropForeignKey(
                name: "FK_CouponRedemption_Coupons_CouponId",
                table: "CouponRedemption");

            migrationBuilder.DropForeignKey(
                name: "FK_ItemUseScenario_Items_ReferenceItemId",
                table: "ItemUseScenario");

            migrationBuilder.DropTable(
                name: "ChannelEntitlementOverwrites");

            migrationBuilder.DropTable(
                name: "DeviceHistories");

            migrationBuilder.DropTable(
                name: "Invites");

            migrationBuilder.DropTable(
                name: "MemberArchetypes");

            migrationBuilder.DropTable(
                name: "Messages");

            migrationBuilder.DropTable(
                name: "Reservation");

            migrationBuilder.DropTable(
                name: "UnreadInventoryItems");

            migrationBuilder.DropTable(
                name: "UserProfiles");

            migrationBuilder.DropTable(
                name: "Channels");

            migrationBuilder.DropTable(
                name: "Archetypes");

            migrationBuilder.DropTable(
                name: "UsersToServerRelations");

            migrationBuilder.DropTable(
                name: "Spaces");

            migrationBuilder.DropTable(
                name: "Users");

            migrationBuilder.DropTable(
                name: "Coupons");

            migrationBuilder.DropTable(
                name: "Items");

            migrationBuilder.DropTable(
                name: "CouponRedemption");

            migrationBuilder.DropTable(
                name: "ItemUseScenario");
        }
    }
}
