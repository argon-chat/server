using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace Argon.Core.Migrations
{
    /// <inheritdoc />
    public partial class ReportSystem : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.CreateTable(
                name: "Reports",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "uuid", nullable: false),
                    ReporterId = table.Column<Guid>(type: "uuid", nullable: false),
                    TargetKind = table.Column<int>(type: "integer", nullable: false),
                    TargetId = table.Column<Guid>(type: "uuid", nullable: false),
                    ChannelId = table.Column<Guid>(type: "uuid", nullable: true),
                    MessageId = table.Column<long>(type: "bigint", nullable: true),
                    Category = table.Column<int>(type: "integer", nullable: false),
                    Reason = table.Column<int>(type: "integer", nullable: false),
                    AdditionalInfo = table.Column<string>(type: "text", maxLength: 2000, nullable: true),
                    Status = table.Column<int>(type: "integer", nullable: false),
                    ReferenceReportId = table.Column<Guid>(type: "uuid", nullable: true),
                    AssignedOperatorId = table.Column<Guid>(type: "uuid", nullable: true),
                    ResolutionNote = table.Column<string>(type: "text", maxLength: 2000, nullable: true),
                    ResolvedAt = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: true),
                    ReporterCredibilityAtTime = table.Column<int>(type: "integer", nullable: false),
                    ReporterIpHash = table.Column<string>(type: "text", maxLength: 64, nullable: true),
                    ReporterAccountAgeDays = table.Column<int>(type: "integer", nullable: false),
                    PriorityScore = table.Column<int>(type: "integer", nullable: false),
                    IsAutoEscalated = table.Column<bool>(type: "boolean", nullable: false),
                    EscalationRule = table.Column<string>(type: "text", maxLength: 64, nullable: true),
                    CreatedAt = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false),
                    UpdatedAt = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false),
                    DeletedAt = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: true),
                    IsDeleted = table.Column<bool>(type: "boolean", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_Reports", x => x.Id);
                    table.ForeignKey(
                        name: "FK_Reports_Users_ReporterId",
                        column: x => x.ReporterId,
                        principalTable: "Users",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Cascade);
                });

            migrationBuilder.CreateTable(
                name: "UserTrustScores",
                columns: table => new
                {
                    UserId = table.Column<Guid>(type: "uuid", nullable: false),
                    TrustScore = table.Column<int>(type: "integer", nullable: false),
                    TotalReportsReceived = table.Column<int>(type: "integer", nullable: false),
                    ConfirmedReportsReceived = table.Column<int>(type: "integer", nullable: false),
                    TotalReportsFiled = table.Column<int>(type: "integer", nullable: false),
                    FalseReportsFiled = table.Column<int>(type: "integer", nullable: false),
                    AutoActionsApplied = table.Column<int>(type: "integer", nullable: false),
                    ContentViolationScore = table.Column<int>(type: "integer", nullable: false),
                    SocialBehaviorScore = table.Column<int>(type: "integer", nullable: false),
                    CommercialAbuseScore = table.Column<int>(type: "integer", nullable: false),
                    PositiveSignalScore = table.Column<int>(type: "integer", nullable: false),
                    ReporterCredibility = table.Column<int>(type: "integer", nullable: false),
                    UniqueReporterCount = table.Column<int>(type: "integer", nullable: false),
                    BlockedByCount = table.Column<int>(type: "integer", nullable: false),
                    LastConfirmedReportAt = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: true),
                    LastRecalculatedAt = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false),
                    CreatedAt = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false),
                    UpdatedAt = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_UserTrustScores", x => x.UserId);
                });

            migrationBuilder.CreateIndex(
                name: "IX_Reports_Category",
                table: "Reports",
                column: "Category");

            migrationBuilder.CreateIndex(
                name: "IX_Reports_CreatedAt",
                table: "Reports",
                column: "CreatedAt");

            migrationBuilder.CreateIndex(
                name: "IX_Reports_ReporterId",
                table: "Reports",
                column: "ReporterId");

            migrationBuilder.CreateIndex(
                name: "IX_Reports_Status",
                table: "Reports",
                column: "Status");

            migrationBuilder.CreateIndex(
                name: "IX_Reports_TargetId",
                table: "Reports",
                column: "TargetId");

            migrationBuilder.CreateIndex(
                name: "idx_reports_dedup",
                table: "Reports",
                columns: new[] { "ReporterId", "TargetId", "Category" });

            migrationBuilder.CreateIndex(
                name: "idx_reports_per_target",
                table: "Reports",
                columns: new[] { "ReporterId", "TargetId" });

            migrationBuilder.CreateIndex(
                name: "idx_reports_priority",
                table: "Reports",
                column: "PriorityScore");

            migrationBuilder.CreateIndex(
                name: "IX_UserTrustScores_ReporterCredibility",
                table: "UserTrustScores",
                column: "ReporterCredibility");

            migrationBuilder.CreateIndex(
                name: "IX_UserTrustScores_TrustScore",
                table: "UserTrustScores",
                column: "TrustScore");
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropTable(
                name: "Reports");

            migrationBuilder.DropTable(
                name: "UserTrustScores");
        }
    }
}
