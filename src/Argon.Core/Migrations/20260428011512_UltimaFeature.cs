using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace Argon.Core.Migrations
{
    /// <inheritdoc />
    public partial class UltimaFeature : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<bool>(
                name: "HasActiveUltima",
                table: "Users",
                type: "boolean",
                nullable: false,
                defaultValue: false);

            migrationBuilder.AddColumn<int>(
                name: "BoostCount",
                table: "Spaces",
                type: "integer",
                nullable: false,
                defaultValue: 0);

            migrationBuilder.AddColumn<int>(
                name: "BoostLevel",
                table: "Spaces",
                type: "integer",
                nullable: false,
                defaultValue: 0);

            migrationBuilder.AddColumn<int>(
                name: "DurationDays",
                table: "ItemUseScenario",
                type: "integer",
                nullable: true);

            migrationBuilder.AddColumn<string>(
                name: "GiftMessage",
                table: "ItemUseScenario",
                type: "text",
                maxLength: 256,
                nullable: true);

            migrationBuilder.CreateTable(
                name: "UltimaSubscriptions",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "uuid", nullable: false),
                    UserId = table.Column<Guid>(type: "uuid", nullable: false),
                    Tier = table.Column<int>(type: "integer", nullable: false),
                    Status = table.Column<int>(type: "integer", nullable: false),
                    StartsAt = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false),
                    ExpiresAt = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false),
                    AutoRenew = table.Column<bool>(type: "boolean", nullable: false),
                    BoostSlots = table.Column<int>(type: "integer", nullable: false),
                    CancelledAt = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: true),
                    XsollaSubscriptionId = table.Column<string>(type: "varchar(256)", maxLength: 256, nullable: true),
                    ActivatedFromItemId = table.Column<Guid>(type: "uuid", nullable: true),
                    CreatedAt = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false),
                    UpdatedAt = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false),
                    DeletedAt = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: true),
                    IsDeleted = table.Column<bool>(type: "boolean", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_UltimaSubscriptions", x => x.Id);
                    table.ForeignKey(
                        name: "FK_UltimaSubscriptions_Users_UserId",
                        column: x => x.UserId,
                        principalTable: "Users",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Cascade);
                });

            migrationBuilder.CreateTable(
                name: "SpaceBoosts",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "uuid", nullable: false),
                    UserId = table.Column<Guid>(type: "uuid", nullable: false),
                    SpaceId = table.Column<Guid>(type: "uuid", nullable: true),
                    SubscriptionId = table.Column<Guid>(type: "uuid", nullable: true),
                    AppliedAt = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: true),
                    TransferCooldownUntil = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: true),
                    Source = table.Column<int>(type: "integer", nullable: false),
                    XsollaTransactionId = table.Column<string>(type: "text", maxLength: 256, nullable: true),
                    CreatedAt = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false),
                    UpdatedAt = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false),
                    DeletedAt = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: true),
                    IsDeleted = table.Column<bool>(type: "boolean", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_SpaceBoosts", x => x.Id);
                    table.ForeignKey(
                        name: "FK_SpaceBoosts_Spaces_SpaceId",
                        column: x => x.SpaceId,
                        principalTable: "Spaces",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.SetNull);
                    table.ForeignKey(
                        name: "FK_SpaceBoosts_UltimaSubscriptions_SubscriptionId",
                        column: x => x.SubscriptionId,
                        principalTable: "UltimaSubscriptions",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.SetNull);
                    table.ForeignKey(
                        name: "FK_SpaceBoosts_Users_UserId",
                        column: x => x.UserId,
                        principalTable: "Users",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Cascade);
                });

            migrationBuilder.UpdateData(
                table: "Spaces",
                keyColumn: "Id",
                keyValue: new Guid("11111111-0000-1111-1111-111111111111"),
                columns: new[] { "BoostCount", "BoostLevel" },
                values: new object[] { 0, 0 });

            migrationBuilder.UpdateData(
                table: "Users",
                keyColumn: "Id",
                keyValue: new Guid("11111111-2222-1111-2222-111111111111"),
                column: "HasActiveUltima",
                value: false);

            migrationBuilder.UpdateData(
                table: "Users",
                keyColumn: "Id",
                keyValue: new Guid("44444444-2222-1111-2222-444444444444"),
                column: "HasActiveUltima",
                value: false);

            migrationBuilder.CreateIndex(
                name: "IX_SpaceBoosts_SpaceId",
                table: "SpaceBoosts",
                column: "SpaceId");

            migrationBuilder.CreateIndex(
                name: "IX_SpaceBoosts_SubscriptionId",
                table: "SpaceBoosts",
                column: "SubscriptionId");

            migrationBuilder.CreateIndex(
                name: "IX_SpaceBoosts_UserId",
                table: "SpaceBoosts",
                column: "UserId");

            migrationBuilder.CreateIndex(
                name: "IX_SpaceBoosts_UserId_SpaceId",
                table: "SpaceBoosts",
                columns: new[] { "UserId", "SpaceId" });

            migrationBuilder.CreateIndex(
                name: "IX_UltimaSubscriptions_Status",
                table: "UltimaSubscriptions",
                column: "Status");

            migrationBuilder.CreateIndex(
                name: "IX_UltimaSubscriptions_UserId",
                table: "UltimaSubscriptions",
                column: "UserId");

            migrationBuilder.CreateIndex(
                name: "IX_UltimaSubscriptions_XsollaSubscriptionId",
                table: "UltimaSubscriptions",
                column: "XsollaSubscriptionId",
                unique: true,
                filter: "\"XsollaSubscriptionId\" IS NOT NULL");
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropTable(
                name: "SpaceBoosts");

            migrationBuilder.DropTable(
                name: "UltimaSubscriptions");

            migrationBuilder.DropColumn(
                name: "HasActiveUltima",
                table: "Users");

            migrationBuilder.DropColumn(
                name: "BoostCount",
                table: "Spaces");

            migrationBuilder.DropColumn(
                name: "BoostLevel",
                table: "Spaces");

            migrationBuilder.DropColumn(
                name: "DurationDays",
                table: "ItemUseScenario");

            migrationBuilder.DropColumn(
                name: "GiftMessage",
                table: "ItemUseScenario");
        }
    }
}
