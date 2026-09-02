using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace Argon.Core.Migrations
{
    /// <inheritdoc />
    public partial class ReportCases : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<Guid>(
                name: "CaseId",
                table: "Reports",
                type: "uuid",
                nullable: true);

            migrationBuilder.AddColumn<Guid>(
                name: "ConversationId",
                table: "Reports",
                type: "uuid",
                nullable: true);

            migrationBuilder.AddColumn<bool>(
                name: "IsIndependent",
                table: "Reports",
                type: "boolean",
                nullable: false,
                defaultValue: false);

            migrationBuilder.AddColumn<string>(
                name: "ReporterDeviceHash",
                table: "Reports",
                type: "text",
                maxLength: 64,
                nullable: true);

            migrationBuilder.AddColumn<Guid>(
                name: "ResolvedByOperatorId",
                table: "Reports",
                type: "uuid",
                nullable: true);

            migrationBuilder.CreateTable(
                name: "ReportCases",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "uuid", nullable: false),
                    GroupKey = table.Column<string>(type: "varchar(128)", maxLength: 128, nullable: false),
                    TargetKind = table.Column<int>(type: "integer", nullable: false),
                    TargetId = table.Column<Guid>(type: "uuid", nullable: false),
                    SpaceId = table.Column<Guid>(type: "uuid", nullable: true),
                    ChannelId = table.Column<Guid>(type: "uuid", nullable: true),
                    MessageId = table.Column<long>(type: "bigint", nullable: true),
                    ConversationId = table.Column<Guid>(type: "uuid", nullable: true),
                    IsOpen = table.Column<bool>(type: "boolean", nullable: false),
                    Status = table.Column<int>(type: "integer", nullable: false),
                    TopCategory = table.Column<int>(type: "integer", nullable: false),
                    PriorityScore = table.Column<int>(type: "integer", nullable: false),
                    ReportCount = table.Column<int>(type: "integer", nullable: false),
                    IndependentReporterCount = table.Column<int>(type: "integer", nullable: false),
                    IsEscalated = table.Column<bool>(type: "boolean", nullable: false),
                    EscalationRule = table.Column<string>(type: "text", maxLength: 64, nullable: true),
                    ContentSnapshot = table.Column<string>(type: "text", nullable: true),
                    AssignedOperatorId = table.Column<Guid>(type: "uuid", nullable: true),
                    ResolvedByOperatorId = table.Column<Guid>(type: "uuid", nullable: true),
                    ResolvedAt = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: true),
                    ResolutionNote = table.Column<string>(type: "text", maxLength: 2000, nullable: true),
                    AppliedAction = table.Column<int>(type: "integer", nullable: false),
                    FirstReportedAt = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false),
                    LastReportedAt = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false),
                    CreatedAt = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false),
                    UpdatedAt = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false),
                    DeletedAt = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: true),
                    IsDeleted = table.Column<bool>(type: "boolean", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_ReportCases", x => x.Id);
                });

            migrationBuilder.CreateIndex(
                name: "IX_Reports_CaseId",
                table: "Reports",
                column: "CaseId");

            migrationBuilder.CreateIndex(
                name: "IX_ReportCases_LastReportedAt",
                table: "ReportCases",
                column: "LastReportedAt");

            migrationBuilder.CreateIndex(
                name: "IX_ReportCases_Status",
                table: "ReportCases",
                column: "Status");

            migrationBuilder.CreateIndex(
                name: "IX_ReportCases_TargetId",
                table: "ReportCases",
                column: "TargetId");

            migrationBuilder.CreateIndex(
                name: "idx_report_cases_open_group",
                table: "ReportCases",
                column: "GroupKey",
                unique: true,
                filter: "\"IsOpen\" = true");

            migrationBuilder.CreateIndex(
                name: "idx_report_cases_queue",
                table: "ReportCases",
                columns: new[] { "IsOpen", "PriorityScore" });

            migrationBuilder.AddForeignKey(
                name: "FK_Reports_ReportCases_CaseId",
                table: "Reports",
                column: "CaseId",
                principalTable: "ReportCases",
                principalColumn: "Id",
                onDelete: ReferentialAction.Restrict);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropForeignKey(
                name: "FK_Reports_ReportCases_CaseId",
                table: "Reports");

            migrationBuilder.DropTable(
                name: "ReportCases");

            migrationBuilder.DropIndex(
                name: "IX_Reports_CaseId",
                table: "Reports");

            migrationBuilder.DropColumn(
                name: "CaseId",
                table: "Reports");

            migrationBuilder.DropColumn(
                name: "ConversationId",
                table: "Reports");

            migrationBuilder.DropColumn(
                name: "IsIndependent",
                table: "Reports");

            migrationBuilder.DropColumn(
                name: "ReporterDeviceHash",
                table: "Reports");

            migrationBuilder.DropColumn(
                name: "ResolvedByOperatorId",
                table: "Reports");
        }
    }
}
