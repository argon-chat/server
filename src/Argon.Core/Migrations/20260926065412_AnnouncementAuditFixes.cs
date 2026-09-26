using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace Argon.Core.Migrations
{
    /// <inheritdoc />
    public partial class AnnouncementAuditFixes : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropIndex(
                name: "ix_channel_read_states_channel_mark",
                table: "ChannelReadStates");

            migrationBuilder.AddColumn<DateTimeOffset>(
                name: "ExpireAt",
                table: "ScheduledPosts",
                type: "timestamp with time zone",
                nullable: true);

            migrationBuilder.AddColumn<DateTimeOffset>(
                name: "ExpireAt",
                table: "MessageDrafts",
                type: "timestamp with time zone",
                nullable: false,
                defaultValue: new DateTimeOffset(new DateTime(1, 1, 1, 0, 0, 0, 0, DateTimeKind.Unspecified), new TimeSpan(0, 0, 0, 0, 0)));

            // Existing rows keep the lifetimes the grains give new ones.
            migrationBuilder.Sql("""UPDATE "MessageDrafts" SET "ExpireAt" = "UpdatedAt" + INTERVAL '30 days';""");
            migrationBuilder.Sql("""UPDATE "ScheduledPosts" SET "ExpireAt" = now() WHERE "Status" IN (1, 3);""");
            migrationBuilder.Sql("""UPDATE "ScheduledPosts" SET "ExpireAt" = now() + INTERVAL '7 days' WHERE "Status" = 2;""");

            migrationBuilder.CreateTable(
                name: "CrosspostDeliveries",
                columns: table => new
                {
                    TargetChannelId = table.Column<Guid>(type: "uuid", nullable: false),
                    SourceChannelId = table.Column<Guid>(type: "uuid", nullable: false),
                    SourceMessageId = table.Column<long>(type: "bigint", nullable: false),
                    MessageId = table.Column<long>(type: "bigint", nullable: true),
                    CreatedAt = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false),
                    ExpireAt = table.Column<DateTimeOffset>(type: "timestamptz", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_CrosspostDeliveries", x => new { x.TargetChannelId, x.SourceChannelId, x.SourceMessageId });
                });

            migrationBuilder.CreateIndex(
                name: "IX_ChannelWebhooks_SpaceId",
                table: "ChannelWebhooks",
                column: "SpaceId");

            migrationBuilder.CreateIndex(
                name: "ix_channel_read_states_channel",
                table: "ChannelReadStates",
                column: "ChannelId")
                .Annotation("Npgsql:CreatedConcurrently", true);

            migrationBuilder.CreateIndex(
                name: "IX_ChannelFollows_SourceSpaceId",
                table: "ChannelFollows",
                column: "SourceSpaceId");

            migrationBuilder.CreateIndex(
                name: "IX_ChannelFollows_TargetSpaceId",
                table: "ChannelFollows",
                column: "TargetSpaceId");
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropTable(
                name: "CrosspostDeliveries");

            migrationBuilder.DropIndex(
                name: "IX_ChannelWebhooks_SpaceId",
                table: "ChannelWebhooks");

            migrationBuilder.DropIndex(
                name: "ix_channel_read_states_channel",
                table: "ChannelReadStates");

            migrationBuilder.DropIndex(
                name: "IX_ChannelFollows_SourceSpaceId",
                table: "ChannelFollows");

            migrationBuilder.DropIndex(
                name: "IX_ChannelFollows_TargetSpaceId",
                table: "ChannelFollows");

            migrationBuilder.DropColumn(
                name: "ExpireAt",
                table: "ScheduledPosts");

            migrationBuilder.DropColumn(
                name: "ExpireAt",
                table: "MessageDrafts");

            migrationBuilder.CreateIndex(
                name: "ix_channel_read_states_channel_mark",
                table: "ChannelReadStates",
                columns: new[] { "ChannelId", "LastReadMessageId" });
        }
    }
}
