using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace Argon.Api.Migrations
{
    /// <inheritdoc />
    public partial class FractionIndex : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<Guid>(
                name: "CategoryId",
                table: "Channels",
                type: "uuid",
                nullable: true);

            migrationBuilder.AddColumn<string>(
                name: "FractionalIndex",
                table: "Channels",
                type: "character varying(64)",
                maxLength: 64,
                nullable: false,
                defaultValue: "");

            migrationBuilder.AddColumn<Guid>(
                name: "SpaceCategoryId",
                table: "ChannelEntitlementOverwrites",
                type: "uuid",
                nullable: true);

            migrationBuilder.CreateTable(
                name: "Categories",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "uuid", nullable: false),
                    ServerId = table.Column<Guid>(type: "uuid", nullable: false),
                    Title = table.Column<string>(type: "character varying(64)", maxLength: 64, nullable: false),
                    CreatedAt = table.Column<long>(type: "bigint", nullable: false),
                    UpdatedAt = table.Column<long>(type: "bigint", nullable: false),
                    DeletedAt = table.Column<long>(type: "bigint", nullable: true),
                    IsDeleted = table.Column<bool>(type: "boolean", nullable: false),
                    CreatorId = table.Column<Guid>(type: "uuid", nullable: false),
                    FractionalIndex = table.Column<string>(type: "character varying(64)", maxLength: 64, nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_Categories", x => x.Id);
                    table.ForeignKey(
                        name: "FK_Categories_Servers_ServerId",
                        column: x => x.ServerId,
                        principalTable: "Servers",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Cascade);
                });

            migrationBuilder.CreateIndex(
                name: "IX_Channels_CategoryId",
                table: "Channels",
                column: "CategoryId");

            migrationBuilder.CreateIndex(
                name: "IX_ChannelEntitlementOverwrites_SpaceCategoryId",
                table: "ChannelEntitlementOverwrites",
                column: "SpaceCategoryId");

            migrationBuilder.CreateIndex(
                name: "IX_Categories_CreatorId",
                table: "Categories",
                column: "CreatorId");

            migrationBuilder.CreateIndex(
                name: "IX_Categories_ServerId",
                table: "Categories",
                column: "ServerId");

            migrationBuilder.AddForeignKey(
                name: "FK_ChannelEntitlementOverwrites_Categories_SpaceCategoryId",
                table: "ChannelEntitlementOverwrites",
                column: "SpaceCategoryId",
                principalTable: "Categories",
                principalColumn: "Id");

            migrationBuilder.AddForeignKey(
                name: "FK_Channels_Categories_CategoryId",
                table: "Channels",
                column: "CategoryId",
                principalTable: "Categories",
                principalColumn: "Id",
                onDelete: ReferentialAction.Cascade);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropForeignKey(
                name: "FK_ChannelEntitlementOverwrites_Categories_SpaceCategoryId",
                table: "ChannelEntitlementOverwrites");

            migrationBuilder.DropForeignKey(
                name: "FK_Channels_Categories_CategoryId",
                table: "Channels");

            migrationBuilder.DropTable(
                name: "Categories");

            migrationBuilder.DropIndex(
                name: "IX_Channels_CategoryId",
                table: "Channels");

            migrationBuilder.DropIndex(
                name: "IX_ChannelEntitlementOverwrites_SpaceCategoryId",
                table: "ChannelEntitlementOverwrites");

            migrationBuilder.DropColumn(
                name: "CategoryId",
                table: "Channels");

            migrationBuilder.DropColumn(
                name: "FractionalIndex",
                table: "Channels");

            migrationBuilder.DropColumn(
                name: "SpaceCategoryId",
                table: "ChannelEntitlementOverwrites");
        }
    }
}
