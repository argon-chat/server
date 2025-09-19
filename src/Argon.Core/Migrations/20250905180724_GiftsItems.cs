using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace Argon.Api.Migrations
{
    /// <inheritdoc />
    public partial class GiftsItems : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.CreateTable(
                name: "CouponRedemption",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "uuid", nullable: false),
                    CouponId = table.Column<Guid>(type: "uuid", nullable: false),
                    UserId = table.Column<Guid>(type: "uuid", nullable: false),
                    RedeemedAt = table.Column<DateTime>(type: "timestamp with time zone", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_CouponRedemption", x => x.Id);
                });

            migrationBuilder.CreateTable(
                name: "Items",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "uuid", nullable: false),
                    TemplateId = table.Column<string>(type: "character varying(255)", maxLength: 255, nullable: false),
                    IsUsable = table.Column<bool>(type: "boolean", nullable: false),
                    IsGiftable = table.Column<bool>(type: "boolean", nullable: false),
                    ItemUseVector = table.Column<int>(type: "integer", nullable: true),
                    ReceivedFrom = table.Column<Guid>(type: "uuid", nullable: true),
                    Data = table.Column<string>(type: "jsonb", maxLength: 1024, nullable: true),
                    OwnerId = table.Column<Guid>(type: "uuid", nullable: false),
                    IsAffectBadge = table.Column<bool>(type: "boolean", nullable: false),
                    TTL = table.Column<TimeSpan>(type: "interval", nullable: true),
                    RedemptionId = table.Column<Guid>(type: "uuid", nullable: true),
                    ConcurrencyToken = table.Column<long>(type: "bigint", nullable: false),
                    CreatedAt = table.Column<long>(type: "bigint", nullable: false),
                    UpdatedAt = table.Column<long>(type: "bigint", nullable: false),
                    DeletedAt = table.Column<long>(type: "bigint", nullable: true),
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
                });

            migrationBuilder.CreateTable(
                name: "Coupons",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "uuid", nullable: false),
                    Code = table.Column<string>(type: "character varying(64)", maxLength: 64, nullable: false),
                    Description = table.Column<string>(type: "character varying(512)", maxLength: 512, nullable: true),
                    ValidFrom = table.Column<DateTime>(type: "timestamp with time zone", nullable: false),
                    ValidTo = table.Column<DateTime>(type: "timestamp with time zone", nullable: false),
                    MaxRedemptions = table.Column<int>(type: "integer", nullable: false),
                    RedemptionCount = table.Column<int>(type: "integer", nullable: false),
                    IsActive = table.Column<bool>(type: "boolean", nullable: false),
                    ReferenceItemEntityId = table.Column<Guid>(type: "uuid", nullable: true),
                    CreatedAt = table.Column<long>(type: "bigint", nullable: false),
                    UpdatedAt = table.Column<long>(type: "bigint", nullable: false),
                    DeletedAt = table.Column<long>(type: "bigint", nullable: true),
                    IsDeleted = table.Column<bool>(type: "boolean", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_Coupons", x => x.Id);
                    table.ForeignKey(
                        name: "FK_Coupons_Items_ReferenceItemEntityId",
                        column: x => x.ReferenceItemEntityId,
                        principalTable: "Items",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.SetNull);
                });

            migrationBuilder.CreateTable(
                name: "UnreadInventoryItems",
                columns: table => new
                {
                    OwnerUserId = table.Column<Guid>(type: "uuid", nullable: false),
                    InventoryItemId = table.Column<Guid>(type: "uuid", nullable: false),
                    CreatedAt = table.Column<long>(type: "bigint", nullable: false),
                    TemplateId = table.Column<string>(type: "text", nullable: false)
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
                name: "IX_Items_TemplateId",
                table: "Items",
                column: "TemplateId");

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

            migrationBuilder.AddForeignKey(
                name: "FK_CouponRedemption_Coupons_CouponId",
                table: "CouponRedemption",
                column: "CouponId",
                principalTable: "Coupons",
                principalColumn: "Id",
                onDelete: ReferentialAction.Cascade);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropForeignKey(
                name: "FK_CouponRedemption_Coupons_CouponId",
                table: "CouponRedemption");

            migrationBuilder.DropTable(
                name: "UnreadInventoryItems");

            migrationBuilder.DropTable(
                name: "Coupons");

            migrationBuilder.DropTable(
                name: "Items");

            migrationBuilder.DropTable(
                name: "CouponRedemption");
        }
    }
}
