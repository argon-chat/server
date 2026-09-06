using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace Argon.Core.Migrations
{
    /// <inheritdoc />
    public partial class UserIgnores : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.CreateTable(
                name: "user_ignores",
                columns: table => new
                {
                    UserId = table.Column<Guid>(type: "uuid", nullable: false),
                    IgnoredId = table.Column<Guid>(type: "uuid", nullable: false),
                    CreatedAt = table.Column<DateTimeOffset>(type: "timestamptz", nullable: false, defaultValueSql: "now()")
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_user_ignores", x => new { x.UserId, x.IgnoredId });
                });

            migrationBuilder.CreateIndex(
                name: "idx_user_ignores_user",
                table: "user_ignores",
                column: "UserId");
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropTable(
                name: "user_ignores");
        }
    }
}
