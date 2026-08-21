using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace Argon.Core.Migrations
{
    /// <inheritdoc />
    public partial class ChannelGroups : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropIndex(
                name: "IX_Channels_SpaceId",
                table: "Channels");

            migrationBuilder.AlterColumn<string>(
                name: "FractionalIndex",
                table: "Channels",
                type: "varchar(64)",
                maxLength: 64,
                nullable: false,
                oldClrType: typeof(string),
                oldType: "text",
                oldMaxLength: 64);

            migrationBuilder.AddColumn<Guid>(
                name: "ChannelGroupId",
                table: "Channels",
                type: "uuid",
                nullable: true);

            migrationBuilder.CreateTable(
                name: "ChannelGroupEntity",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "uuid", nullable: false),
                    SpaceId = table.Column<Guid>(type: "uuid", nullable: false),
                    Name = table.Column<string>(type: "text", maxLength: 128, nullable: false),
                    Description = table.Column<string>(type: "text", maxLength: 512, nullable: true),
                    IsCollapsed = table.Column<bool>(type: "boolean", nullable: false),
                    CreatedAt = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false),
                    UpdatedAt = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false),
                    DeletedAt = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: true),
                    IsDeleted = table.Column<bool>(type: "boolean", nullable: false),
                    CreatorId = table.Column<Guid>(type: "uuid", nullable: false),
                    FractionalIndex = table.Column<string>(type: "varchar(64)", maxLength: 64, nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_ChannelGroupEntity", x => x.Id);
                    table.ForeignKey(
                        name: "FK_ChannelGroupEntity_Spaces_SpaceId",
                        column: x => x.SpaceId,
                        principalTable: "Spaces",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Cascade);
                });

            migrationBuilder.CreateIndex(
                name: "IX_Channels_ChannelGroupId",
                table: "Channels",
                column: "ChannelGroupId");

            migrationBuilder.CreateIndex(
                name: "IX_Channels_SpaceId_ChannelGroupId_FractionalIndex",
                table: "Channels",
                columns: new[] { "SpaceId", "ChannelGroupId", "FractionalIndex" });

            migrationBuilder.CreateIndex(
                name: "IX_ChannelGroupEntity_CreatorId",
                table: "ChannelGroupEntity",
                column: "CreatorId");

            migrationBuilder.CreateIndex(
                name: "IX_ChannelGroupEntity_SpaceId_FractionalIndex",
                table: "ChannelGroupEntity",
                columns: new[] { "SpaceId", "FractionalIndex" });

            migrationBuilder.AddForeignKey(
                name: "FK_Channels_ChannelGroupEntity_ChannelGroupId",
                table: "Channels",
                column: "ChannelGroupId",
                principalTable: "ChannelGroupEntity",
                principalColumn: "Id",
                onDelete: ReferentialAction.SetNull);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropForeignKey(
                name: "FK_Channels_ChannelGroupEntity_ChannelGroupId",
                table: "Channels");

            migrationBuilder.DropTable(
                name: "ChannelGroupEntity");

            migrationBuilder.DropIndex(
                name: "IX_Channels_ChannelGroupId",
                table: "Channels");

            migrationBuilder.DropIndex(
                name: "IX_Channels_SpaceId_ChannelGroupId_FractionalIndex",
                table: "Channels");

            migrationBuilder.DropColumn(
                name: "ChannelGroupId",
                table: "Channels");

            migrationBuilder.AlterColumn<string>(
                name: "FractionalIndex",
                table: "Channels",
                type: "text",
                maxLength: 64,
                nullable: false,
                oldClrType: typeof(string),
                oldType: "varchar(64)",
                oldMaxLength: 64);

            migrationBuilder.CreateIndex(
                name: "IX_Channels_SpaceId",
                table: "Channels",
                column: "SpaceId");
        }
    }
}
