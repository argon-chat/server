using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace Argon.Core.Migrations
{
    /// <inheritdoc />
    public partial class GifsFeature : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.CreateTable(
                name: "SavedGifs",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "uuid", nullable: false),
                    UserId = table.Column<Guid>(type: "uuid", nullable: false),
                    Slug = table.Column<string>(type: "varchar(256)", maxLength: 256, nullable: true),
                    FileId = table.Column<Guid>(type: "uuid", nullable: false),
                    Width = table.Column<int>(type: "integer", nullable: false),
                    Height = table.Column<int>(type: "integer", nullable: false),
                    AddedAt = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false),
                    CreatedAt = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false),
                    UpdatedAt = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false),
                    DeletedAt = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: true),
                    IsDeleted = table.Column<bool>(type: "boolean", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_SavedGifs", x => x.Id);
                });

            migrationBuilder.CreateIndex(
                name: "IX_SavedGifs_FileId",
                table: "SavedGifs",
                column: "FileId");

            migrationBuilder.CreateIndex(
                name: "IX_SavedGifs_UserId",
                table: "SavedGifs",
                column: "UserId");

            migrationBuilder.CreateIndex(
                name: "IX_SavedGifs_UserId_Slug",
                table: "SavedGifs",
                columns: new[] { "UserId", "Slug" },
                unique: true,
                filter: "\"Slug\" IS NOT NULL");
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropTable(
                name: "SavedGifs");
        }
    }
}
