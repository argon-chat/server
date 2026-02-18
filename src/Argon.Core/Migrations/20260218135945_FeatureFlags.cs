using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace Argon.Core.Migrations
{
    /// <inheritdoc />
    public partial class FeatureFlags : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.CreateTable(
                name: "FeatureFlags",
                columns: table => new
                {
                    Id = table.Column<string>(type: "text", maxLength: 128, nullable: false),
                    Description = table.Column<string>(type: "text", maxLength: 512, nullable: true),
                    DefaultEnabled = table.Column<bool>(type: "boolean", nullable: false),
                    RolloutPercentage = table.Column<int>(type: "integer", nullable: true),
                    Variants = table.Column<string>(type: "text", maxLength: 2048, nullable: true),
                    ExpiresAt = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: true),
                    CreatedAt = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false),
                    UpdatedAt = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false),
                    DeletedAt = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: true),
                    IsDeleted = table.Column<bool>(type: "boolean", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_FeatureFlags", x => x.Id);
                });

            migrationBuilder.CreateTable(
                name: "FeatureFlagOverrides",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "uuid", nullable: false),
                    FeatureFlagId = table.Column<string>(type: "varchar(128)", maxLength: 128, nullable: false),
                    Scope = table.Column<int>(type: "integer", nullable: false),
                    TargetId = table.Column<string>(type: "varchar(128)", maxLength: 128, nullable: false),
                    Enabled = table.Column<bool>(type: "boolean", nullable: true),
                    RolloutPercentage = table.Column<int>(type: "integer", nullable: true),
                    ForcedVariant = table.Column<string>(type: "text", maxLength: 64, nullable: true),
                    CreatedAt = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false),
                    UpdatedAt = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false),
                    DeletedAt = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: true),
                    IsDeleted = table.Column<bool>(type: "boolean", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_FeatureFlagOverrides", x => x.Id);
                    table.ForeignKey(
                        name: "FK_FeatureFlagOverrides_FeatureFlags_FeatureFlagId",
                        column: x => x.FeatureFlagId,
                        principalTable: "FeatureFlags",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Cascade);
                });

            migrationBuilder.CreateIndex(
                name: "IX_FeatureFlagOverrides_FeatureFlagId_Scope_TargetId",
                table: "FeatureFlagOverrides",
                columns: new[] { "FeatureFlagId", "Scope", "TargetId" },
                unique: true);

            migrationBuilder.CreateIndex(
                name: "IX_FeatureFlagOverrides_Scope",
                table: "FeatureFlagOverrides",
                column: "Scope");

            migrationBuilder.CreateIndex(
                name: "IX_FeatureFlagOverrides_TargetId",
                table: "FeatureFlagOverrides",
                column: "TargetId");

            migrationBuilder.CreateIndex(
                name: "IX_FeatureFlags_DefaultEnabled",
                table: "FeatureFlags",
                column: "DefaultEnabled");

            migrationBuilder.CreateIndex(
                name: "IX_FeatureFlags_ExpiresAt",
                table: "FeatureFlags",
                column: "ExpiresAt");
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropTable(
                name: "FeatureFlagOverrides");

            migrationBuilder.DropTable(
                name: "FeatureFlags");
        }
    }
}
