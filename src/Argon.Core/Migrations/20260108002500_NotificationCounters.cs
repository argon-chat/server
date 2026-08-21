using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace Argon.Core.Migrations
{
    /// <inheritdoc />
    public partial class NotificationCounters : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.CreateTable(
                name: "NotificationCounters",
                columns: table => new
                {
                    UserId = table.Column<Guid>(type: "uuid", nullable: false),
                    CounterType = table.Column<string>(type: "text", maxLength: 64, nullable: false),
                    Count = table.Column<long>(type: "bigint", nullable: false),
                    UpdatedAt = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_NotificationCounters", x => new { x.UserId, x.CounterType });
                });

            migrationBuilder.CreateIndex(
                name: "ix_notification_counters_updated_at",
                table: "NotificationCounters",
                column: "UpdatedAt");

            migrationBuilder.CreateIndex(
                name: "ix_notification_counters_user_id",
                table: "NotificationCounters",
                column: "UserId");
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropTable(
                name: "NotificationCounters");
        }
    }
}
