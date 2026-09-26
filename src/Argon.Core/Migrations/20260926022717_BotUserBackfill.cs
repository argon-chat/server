using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace Argon.Core.Migrations
{
    /// <inheritdoc />
    public partial class BotUserBackfill : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            // Bots created through the dev console never had Users.BotEntityId set, so they lost the BOT flag.
            migrationBuilder.Sql(
                """
                UPDATE "Users" u
                SET "BotEntityId" = b."AppId"
                FROM "Bots" b
                WHERE b."BotAsUserId" = u."Id" AND u."BotEntityId" IS NULL;
                """);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {

        }
    }
}
