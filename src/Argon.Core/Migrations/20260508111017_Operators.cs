using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace Argon.Core.Migrations
{
    /// <inheritdoc />
    public partial class Operators : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.CreateTable(
                name: "Operators",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "uuid", nullable: false),
                    DisplayName = table.Column<string>(type: "text", maxLength: 256, nullable: false),
                    Email = table.Column<string>(type: "varchar(256)", maxLength: 256, nullable: false),
                    UserId = table.Column<Guid>(type: "uuid", nullable: true),
                    CertificateSerialNumber = table.Column<string>(type: "varchar(128)", maxLength: 128, nullable: true),
                    CertificateThumbprint = table.Column<string>(type: "varchar(128)", maxLength: 128, nullable: true),
                    CertificateSubject = table.Column<string>(type: "text", maxLength: 512, nullable: true),
                    CertificateNotBefore = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: true),
                    CertificateNotAfter = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: true),
                    IsActive = table.Column<bool>(type: "boolean", nullable: false),
                    LastAuthAt = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: true),
                    CreatedAt = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false),
                    UpdatedAt = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false),
                    DeletedAt = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: true),
                    IsDeleted = table.Column<bool>(type: "boolean", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_Operators", x => x.Id);
                    table.ForeignKey(
                        name: "FK_Operators_Users_UserId",
                        column: x => x.UserId,
                        principalTable: "Users",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.SetNull);
                });

            migrationBuilder.CreateIndex(
                name: "IX_Operators_CertificateSerialNumber",
                table: "Operators",
                column: "CertificateSerialNumber");

            migrationBuilder.CreateIndex(
                name: "IX_Operators_CertificateThumbprint",
                table: "Operators",
                column: "CertificateThumbprint");

            migrationBuilder.CreateIndex(
                name: "IX_Operators_Email",
                table: "Operators",
                column: "Email",
                unique: true);

            migrationBuilder.CreateIndex(
                name: "IX_Operators_UserId",
                table: "Operators",
                column: "UserId",
                unique: true);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropTable(
                name: "Operators");
        }
    }
}
