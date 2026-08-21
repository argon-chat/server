using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace Argon.Core.Migrations
{
    /// <inheritdoc />
    public partial class OperatorsAuditLog : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.CreateTable(
                name: "OperatorAuditLog",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "uuid", nullable: false),
                    OperatorId = table.Column<Guid>(type: "uuid", nullable: false),
                    OperatorEmail = table.Column<string>(type: "text", maxLength: 256, nullable: false),
                    Action = table.Column<string>(type: "varchar(128)", maxLength: 128, nullable: false),
                    TargetType = table.Column<string>(type: "text", maxLength: 128, nullable: true),
                    TargetId = table.Column<string>(type: "varchar(256)", maxLength: 256, nullable: true),
                    Details = table.Column<string>(type: "text", maxLength: 4096, nullable: true),
                    CreatedAt = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false),
                    UpdatedAt = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false),
                    DeletedAt = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: true),
                    IsDeleted = table.Column<bool>(type: "boolean", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_OperatorAuditLog", x => x.Id);
                });

            migrationBuilder.CreateIndex(
                name: "IX_OperatorAuditLog_Action",
                table: "OperatorAuditLog",
                column: "Action");

            migrationBuilder.CreateIndex(
                name: "IX_OperatorAuditLog_CreatedAt",
                table: "OperatorAuditLog",
                column: "CreatedAt");

            migrationBuilder.CreateIndex(
                name: "IX_OperatorAuditLog_OperatorId",
                table: "OperatorAuditLog",
                column: "OperatorId");

            migrationBuilder.CreateIndex(
                name: "IX_OperatorAuditLog_TargetId",
                table: "OperatorAuditLog",
                column: "TargetId");
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropTable(
                name: "OperatorAuditLog");
        }
    }
}
