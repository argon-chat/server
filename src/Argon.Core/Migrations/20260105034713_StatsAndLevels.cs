using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace Argon.Core.Migrations
{
    /// <inheritdoc />
    public partial class StatsAndLevels : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.CreateTable(
                name: "UserDailyStats",
                columns: table => new
                {
                    UserId = table.Column<Guid>(type: "uuid", nullable: false),
                    Date = table.Column<DateOnly>(type: "date", nullable: false),
                    TimeInVoiceSeconds = table.Column<int>(type: "integer", nullable: false),
                    CallsMade = table.Column<int>(type: "integer", nullable: false),
                    MessagesSent = table.Column<int>(type: "integer", nullable: false),
                    XpEarned = table.Column<int>(type: "integer", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_UserDailyStats", x => new { x.UserId, x.Date });
                });

            migrationBuilder.CreateTable(
                name: "UserLevels",
                columns: table => new
                {
                    UserId = table.Column<Guid>(type: "uuid", nullable: false),
                    TotalXpAllTime = table.Column<long>(type: "bigint", nullable: false),
                    CurrentCycleXp = table.Column<int>(type: "integer", nullable: false),
                    CurrentLevel = table.Column<int>(type: "integer", nullable: false),
                    LastXpAward = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false),
                    CanClaimMedal = table.Column<bool>(type: "boolean", nullable: false),
                    CreatedAt = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false),
                    UpdatedAt = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_UserLevels", x => x.UserId);
                });

            migrationBuilder.CreateIndex(
                name: "IX_UserDailyStats_Date",
                table: "UserDailyStats",
                column: "Date");

            migrationBuilder.CreateIndex(
                name: "IX_UserDailyStats_UserId",
                table: "UserDailyStats",
                column: "UserId");

            migrationBuilder.CreateIndex(
                name: "IX_UserDailyStats_UserId_Date",
                table: "UserDailyStats",
                columns: new[] { "UserId", "Date" },
                descending: new[] { false, true });

            migrationBuilder.CreateIndex(
                name: "IX_UserLevels_CanClaimMedal",
                table: "UserLevels",
                column: "CanClaimMedal");

            migrationBuilder.CreateIndex(
                name: "IX_UserLevels_CurrentLevel",
                table: "UserLevels",
                column: "CurrentLevel");

            migrationBuilder.CreateIndex(
                name: "IX_UserLevels_TotalXpAllTime",
                table: "UserLevels",
                column: "TotalXpAllTime");
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropTable(
                name: "UserDailyStats");

            migrationBuilder.DropTable(
                name: "UserLevels");
        }
    }
}
