using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace Argon.Core.Migrations
{
    /// <inheritdoc />
    public partial class DropPendingEmailChanges : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropTable(
                name: "PendingEmailChanges");
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.CreateTable(
                name: "PendingEmailChanges",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "uuid", nullable: false),
                    UserId = table.Column<Guid>(type: "uuid", nullable: false),
                    AttemptsLeft = table.Column<int>(type: "integer", nullable: false),
                    CodeHash = table.Column<string>(type: "text", maxLength: 512, nullable: false),
                    CodeSalt = table.Column<string>(type: "text", maxLength: 128, nullable: false),
                    CreatedAt = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false),
                    DeletedAt = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: true),
                    ExpiresAt = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false),
                    IsDeleted = table.Column<bool>(type: "boolean", nullable: false),
                    NewEmail = table.Column<string>(type: "text", maxLength: 255, nullable: false),
                    UpdatedAt = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_PendingEmailChanges", x => x.Id);
                    table.ForeignKey(
                        name: "FK_PendingEmailChanges_Users_UserId",
                        column: x => x.UserId,
                        principalTable: "Users",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Cascade);
                });

            migrationBuilder.CreateIndex(
                name: "IX_PendingEmailChanges_ExpiresAt",
                table: "PendingEmailChanges",
                column: "ExpiresAt");

            migrationBuilder.CreateIndex(
                name: "IX_PendingEmailChanges_UserId",
                table: "PendingEmailChanges",
                column: "UserId");
        }
    }
}
