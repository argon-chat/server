using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace Argon.Core.Migrations
{
    /// <inheritdoc />
    public partial class ChannelLastMessages : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.CreateTable(
                name: "ChannelLastMessages",
                columns: table => new
                {
                    ChannelId = table.Column<Guid>(type: "uuid", nullable: false),
                    SpaceId = table.Column<Guid>(type: "uuid", nullable: false),
                    LastMessageId = table.Column<long>(type: "bigint", nullable: false),
                    UpdatedAt = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_ChannelLastMessages", x => x.ChannelId);
                });

            migrationBuilder.CreateIndex(
                name: "ix_channel_last_messages_space",
                table: "ChannelLastMessages",
                column: "SpaceId");

            // Hand-written; the scaffolder does not produce it, and without it this migration silently
            // erases unread state on the deploy that applies it.
            //
            // Every reader of the high-water mark now takes this table as its floor, and treats a
            // missing row as zero. "Channels"."LastMessageId" holds the live value for every channel
            // with traffic, so an empty table means a channel last posted in a week ago compares 0
            // against the member's read cursor, loses, and its badge disappears — for everyone, until
            // somebody posts there again. The Redis cell masks it only for channels written since the
            // cell shipped, and two readers (the admin space card, and the grain's own stored-mark
            // read) never consult the cell at all.
            //
            // ON CONFLICT DO NOTHING rather than an upsert: channel grains are flushing into this table
            // while the migration runs, and a live flush carries a newer id than the column does. The
            // loser of that race must be this statement, always.
            migrationBuilder.Sql(
                """
                INSERT INTO "ChannelLastMessages" ("ChannelId", "SpaceId", "LastMessageId", "UpdatedAt")
                SELECT "Id", "SpaceId", "LastMessageId", now()
                  FROM "Channels"
                 WHERE "LastMessageId" > 0
                ON CONFLICT ("ChannelId") DO NOTHING;
                """);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropTable(
                name: "ChannelLastMessages");
        }
    }
}
