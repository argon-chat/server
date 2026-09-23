using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace Argon.Core.Migrations
{
    /// <inheritdoc />
    public partial class CosmeticEquips : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.CreateTable(
                name: "CosmeticEquips",
                columns: table => new
                {
                    UserId = table.Column<Guid>(type: "uuid", nullable: false),
                    KindKey = table.Column<string>(type: "text", maxLength: 64, nullable: false),
                    SlotIndex = table.Column<int>(type: "integer", nullable: false),
                    CosmeticItemId = table.Column<Guid>(type: "uuid", nullable: true),
                    Choices = table.Column<string>(type: "jsonb", nullable: true),
                    Tuning = table.Column<string>(type: "jsonb", nullable: true),
                    UpdatedAt = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_CosmeticEquips", x => new { x.UserId, x.KindKey, x.SlotIndex });
                    table.ForeignKey(
                        name: "FK_CosmeticEquips_Cosmetics_CosmeticItemId",
                        column: x => x.CosmeticItemId,
                        principalTable: "Cosmetics",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Cascade);
                    table.ForeignKey(
                        name: "FK_CosmeticEquips_Users_UserId",
                        column: x => x.UserId,
                        principalTable: "Users",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Cascade);
                });

            migrationBuilder.CreateIndex(
                name: "IX_CosmeticEquips_CosmeticItemId",
                table: "CosmeticEquips",
                column: "CosmeticItemId");
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropTable(
                name: "CosmeticEquips");
        }
    }
}
