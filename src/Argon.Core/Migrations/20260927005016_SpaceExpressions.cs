using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace Argon.Core.Migrations
{
    /// <inheritdoc />
    public partial class SpaceExpressions : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.CreateTable(
                name: "ExpressionPacks",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "uuid", nullable: false),
                    SpaceId = table.Column<Guid>(type: "uuid", nullable: false),
                    Kind = table.Column<int>(type: "integer", nullable: false),
                    Title = table.Column<string>(type: "text", maxLength: 64, nullable: false),
                    Slug = table.Column<string>(type: "varchar(64)", maxLength: 64, nullable: false),
                    CoverItemId = table.Column<Guid>(type: "uuid", nullable: true),
                    SortOrder = table.Column<int>(type: "integer", nullable: false),
                    Version = table.Column<long>(type: "bigint", nullable: false),
                    ItemCount = table.Column<int>(type: "integer", nullable: false),
                    CreatedAt = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false),
                    UpdatedAt = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false),
                    DeletedAt = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: true),
                    IsDeleted = table.Column<bool>(type: "boolean", nullable: false),
                    CreatorId = table.Column<Guid>(type: "uuid", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_ExpressionPacks", x => x.Id);
                    table.ForeignKey(
                        name: "FK_ExpressionPacks_Spaces_SpaceId",
                        column: x => x.SpaceId,
                        principalTable: "Spaces",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Cascade);
                });

            migrationBuilder.CreateTable(
                name: "ExpressionItems",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "uuid", nullable: false),
                    PackId = table.Column<Guid>(type: "uuid", nullable: false),
                    SpaceId = table.Column<Guid>(type: "uuid", nullable: false),
                    Kind = table.Column<int>(type: "integer", nullable: false),
                    Format = table.Column<int>(type: "integer", nullable: false),
                    Name = table.Column<string>(type: "varchar(64)", maxLength: 64, nullable: false),
                    FileId = table.Column<Guid>(type: "uuid", nullable: false),
                    ThumbFileId = table.Column<Guid>(type: "uuid", nullable: true),
                    Width = table.Column<int>(type: "integer", nullable: false),
                    Height = table.Column<int>(type: "integer", nullable: false),
                    FileSize = table.Column<int>(type: "integer", nullable: false),
                    Emoji = table.Column<string>(type: "jsonb", nullable: false),
                    Keywords = table.Column<string>(type: "jsonb", nullable: false),
                    Outline = table.Column<byte[]>(type: "bytea", nullable: true),
                    TextColor = table.Column<bool>(type: "boolean", nullable: false),
                    SortOrder = table.Column<int>(type: "integer", nullable: false),
                    CreatedAt = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false),
                    UpdatedAt = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false),
                    DeletedAt = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: true),
                    IsDeleted = table.Column<bool>(type: "boolean", nullable: false),
                    CreatorId = table.Column<Guid>(type: "uuid", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_ExpressionItems", x => x.Id);
                    table.ForeignKey(
                        name: "FK_ExpressionItems_ExpressionPacks_PackId",
                        column: x => x.PackId,
                        principalTable: "ExpressionPacks",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Cascade);
                });

            migrationBuilder.CreateIndex(
                name: "IX_ExpressionItems_CreatorId",
                table: "ExpressionItems",
                column: "CreatorId");

            migrationBuilder.CreateIndex(
                name: "IX_ExpressionItems_PackId_SortOrder",
                table: "ExpressionItems",
                columns: new[] { "PackId", "SortOrder" });

            migrationBuilder.CreateIndex(
                name: "IX_ExpressionItems_SpaceId_Kind_IsDeleted",
                table: "ExpressionItems",
                columns: new[] { "SpaceId", "Kind", "IsDeleted" });

            migrationBuilder.CreateIndex(
                name: "IX_ExpressionItems_SpaceId_Name",
                table: "ExpressionItems",
                columns: new[] { "SpaceId", "Name" },
                unique: true,
                filter: "\"Kind\" = 1 AND \"IsDeleted\" = false");

            migrationBuilder.CreateIndex(
                name: "IX_ExpressionPacks_CreatorId",
                table: "ExpressionPacks",
                column: "CreatorId");

            migrationBuilder.CreateIndex(
                name: "IX_ExpressionPacks_SpaceId_Kind_IsDeleted",
                table: "ExpressionPacks",
                columns: new[] { "SpaceId", "Kind", "IsDeleted" });

            migrationBuilder.CreateIndex(
                name: "IX_ExpressionPacks_SpaceId_Slug",
                table: "ExpressionPacks",
                columns: new[] { "SpaceId", "Slug" },
                unique: true,
                filter: "\"IsDeleted\" = false");
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropTable(
                name: "ExpressionItems");

            migrationBuilder.DropTable(
                name: "ExpressionPacks");
        }
    }
}
