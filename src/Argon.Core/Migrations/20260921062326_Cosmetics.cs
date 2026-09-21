using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace Argon.Core.Migrations
{
    /// <inheritdoc />
    public partial class Cosmetics : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.CreateTable(
                name: "Cosmetics",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "uuid", nullable: false),
                    KindKey = table.Column<string>(type: "varchar(64)", maxLength: 64, nullable: false),
                    Slug = table.Column<string>(type: "varchar(128)", maxLength: 128, nullable: false),
                    NameKey = table.Column<string>(type: "text", maxLength: 128, nullable: false),
                    DescriptionKey = table.Column<string>(type: "text", maxLength: 128, nullable: true),
                    Rarity = table.Column<string>(type: "text", maxLength: 32, nullable: true),
                    SortOrder = table.Column<int>(type: "integer", nullable: false),
                    Version = table.Column<int>(type: "integer", nullable: false),
                    Payload = table.Column<string>(type: "text", nullable: false),
                    AssetFileIds = table.Column<string>(type: "jsonb", nullable: false),
                    IsEnabled = table.Column<bool>(type: "boolean", nullable: false),
                    IsPublished = table.Column<bool>(type: "boolean", nullable: false),
                    PublishedAt = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: true),
                    AvailableFrom = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: true),
                    AvailableUntil = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: true),
                    AcquisitionMode = table.Column<int>(type: "integer", nullable: false),
                    UltimaTierRequired = table.Column<int>(type: "integer", nullable: true),
                    CreatedAt = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false),
                    UpdatedAt = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false),
                    DeletedAt = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: true),
                    IsDeleted = table.Column<bool>(type: "boolean", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_Cosmetics", x => x.Id);
                });

            migrationBuilder.CreateTable(
                name: "CosmeticOwnerships",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "uuid", nullable: false),
                    UserId = table.Column<Guid>(type: "uuid", nullable: false),
                    CosmeticItemId = table.Column<Guid>(type: "uuid", nullable: false),
                    Source = table.Column<int>(type: "integer", nullable: false),
                    ExpiresAt = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: true),
                    RevokedAt = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: true),
                    CreatedAt = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false),
                    UpdatedAt = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false),
                    DeletedAt = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: true),
                    IsDeleted = table.Column<bool>(type: "boolean", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_CosmeticOwnerships", x => x.Id);
                    table.ForeignKey(
                        name: "FK_CosmeticOwnerships_Cosmetics_CosmeticItemId",
                        column: x => x.CosmeticItemId,
                        principalTable: "Cosmetics",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Cascade);
                    table.ForeignKey(
                        name: "FK_CosmeticOwnerships_Users_UserId",
                        column: x => x.UserId,
                        principalTable: "Users",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Cascade);
                });

            migrationBuilder.CreateTable(
                name: "CosmeticTranslations",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "uuid", nullable: false),
                    CosmeticItemId = table.Column<Guid>(type: "uuid", nullable: false),
                    Locale = table.Column<string>(type: "varchar(16)", maxLength: 16, nullable: false),
                    Name = table.Column<string>(type: "text", maxLength: 128, nullable: false),
                    Description = table.Column<string>(type: "text", maxLength: 512, nullable: true),
                    CreatedAt = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false),
                    UpdatedAt = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false),
                    DeletedAt = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: true),
                    IsDeleted = table.Column<bool>(type: "boolean", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_CosmeticTranslations", x => x.Id);
                    table.ForeignKey(
                        name: "FK_CosmeticTranslations_Cosmetics_CosmeticItemId",
                        column: x => x.CosmeticItemId,
                        principalTable: "Cosmetics",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Cascade);
                });

            migrationBuilder.CreateIndex(
                name: "IX_CosmeticOwnerships_CosmeticItemId",
                table: "CosmeticOwnerships",
                column: "CosmeticItemId");

            migrationBuilder.CreateIndex(
                name: "IX_CosmeticOwnerships_ExpiresAt",
                table: "CosmeticOwnerships",
                column: "ExpiresAt",
                filter: "\"ExpiresAt\" IS NOT NULL AND \"RevokedAt\" IS NULL");

            migrationBuilder.CreateIndex(
                name: "IX_CosmeticOwnerships_UserId_CosmeticItemId",
                table: "CosmeticOwnerships",
                columns: new[] { "UserId", "CosmeticItemId" },
                unique: true,
                filter: "\"RevokedAt\" IS NULL AND \"IsDeleted\" = false");

            migrationBuilder.CreateIndex(
                name: "IX_CosmeticTranslations_CosmeticItemId_Locale",
                table: "CosmeticTranslations",
                columns: new[] { "CosmeticItemId", "Locale" },
                unique: true,
                filter: "\"IsDeleted\" = false");

            migrationBuilder.CreateIndex(
                name: "IX_Cosmetics_IsPublished_AvailableUntil",
                table: "Cosmetics",
                columns: new[] { "IsPublished", "AvailableUntil" });

            migrationBuilder.CreateIndex(
                name: "IX_Cosmetics_KindKey_IsPublished_IsEnabled",
                table: "Cosmetics",
                columns: new[] { "KindKey", "IsPublished", "IsEnabled" });

            migrationBuilder.CreateIndex(
                name: "IX_Cosmetics_KindKey_Slug",
                table: "Cosmetics",
                columns: new[] { "KindKey", "Slug" },
                unique: true,
                filter: "\"IsDeleted\" = false");
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropTable(
                name: "CosmeticOwnerships");

            migrationBuilder.DropTable(
                name: "CosmeticTranslations");

            migrationBuilder.DropTable(
                name: "Cosmetics");
        }
    }
}
