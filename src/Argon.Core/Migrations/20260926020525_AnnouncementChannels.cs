using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace Argon.Core.Migrations
{
    /// <inheritdoc />
    public partial class AnnouncementChannels : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<DateTimeOffset>(
                name: "EditedAt",
                table: "Messages",
                type: "timestamp with time zone",
                nullable: true);

            // Announcement channels are read-only for @everyone: add SendMessages to the everyone
            // overwrite's Deny, or create that overwrite, unless it explicitly allows SendMessages.
            migrationBuilder.Sql(
                """
                UPDATE "ChannelEntitlementOverwrites" o
                SET "Deny" = o."Deny" + 32, "UpdatedAt" = now()
                FROM "Channels" c, "Archetypes" a
                WHERE o."ChannelId" = c."Id" AND o."ArchetypeId" = a."Id"
                  AND a."SpaceId" = c."SpaceId" AND a."IsDefault" AND NOT a."IsDeleted"
                  AND c."ChannelType" = 2 AND NOT c."IsDeleted" AND NOT o."IsDeleted"
                  AND floor(o."Deny" / 32) % 2 = 0 AND floor(o."Allow" / 32) % 2 = 0;
                """);

            migrationBuilder.Sql(
                """
                INSERT INTO "ChannelEntitlementOverwrites"
                    ("Id", "ChannelId", "Scope", "ArchetypeId", "SpaceMemberId", "Allow", "Deny",
                     "CreatorId", "CreatedAt", "UpdatedAt", "IsDeleted", "DeletedAt")
                SELECT gen_random_uuid(), c."Id", 0, a."Id", NULL, 0, 32,
                       '00000000-0000-0000-0000-000000000000', now(), now(), false, NULL
                FROM "Channels" c
                JOIN "Archetypes" a ON a."SpaceId" = c."SpaceId" AND a."IsDefault" AND NOT a."IsDeleted"
                WHERE c."ChannelType" = 2 AND NOT c."IsDeleted"
                  AND NOT EXISTS (
                      SELECT 1 FROM "ChannelEntitlementOverwrites" o
                      WHERE o."ChannelId" = c."Id" AND o."ArchetypeId" = a."Id" AND NOT o."IsDeleted");
                """);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropColumn(
                name: "EditedAt",
                table: "Messages");
        }
    }
}
