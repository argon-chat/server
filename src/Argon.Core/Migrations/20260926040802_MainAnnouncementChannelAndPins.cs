using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace Argon.Core.Migrations
{
    /// <inheritdoc />
    public partial class MainAnnouncementChannelAndPins : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<Guid>(
                name: "MainAnnouncementChannelId",
                table: "Spaces",
                type: "uuid",
                nullable: true);

            migrationBuilder.CreateTable(
                name: "ChannelPins",
                columns: table => new
                {
                    ChannelId = table.Column<Guid>(type: "uuid", nullable: false),
                    MessageId = table.Column<long>(type: "BIGINT", nullable: false),
                    SpaceId = table.Column<Guid>(type: "uuid", nullable: false),
                    PinnedBy = table.Column<Guid>(type: "uuid", nullable: false),
                    PinnedAt = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_ChannelPins", x => new { x.ChannelId, x.MessageId });
                });

            migrationBuilder.UpdateData(
                table: "Spaces",
                keyColumn: "Id",
                keyValue: new Guid("11111111-0000-1111-1111-111111111111"),
                column: "MainAnnouncementChannelId",
                value: null);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropTable(
                name: "ChannelPins");

            migrationBuilder.DropColumn(
                name: "MainAnnouncementChannelId",
                table: "Spaces");
        }
    }
}
