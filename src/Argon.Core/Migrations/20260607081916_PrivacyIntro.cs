using System;
using System.Collections.Generic;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace Argon.Core.Migrations
{
    /// <inheritdoc />
    public partial class PrivacyIntro : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.CreateTable(
                name: "privacy_rule_entity",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "uuid", nullable: false),
                    UserId = table.Column<Guid>(type: "uuid", nullable: false),
                    Key = table.Column<string>(type: "varchar(128)", maxLength: 128, nullable: false),
                    Mode = table.Column<int>(type: "integer", nullable: false),
                    ScopeSpaceId = table.Column<Guid>(type: "uuid", nullable: true),
                    AllowExceptions = table.Column<List<Guid>>(type: "uuid[]", nullable: false),
                    DenyExceptions = table.Column<List<Guid>>(type: "uuid[]", nullable: false),
                    CreatedAt = table.Column<DateTimeOffset>(type: "timestamptz", nullable: false, defaultValueSql: "now()"),
                    UpdatedAt = table.Column<DateTimeOffset>(type: "timestamptz", nullable: false, defaultValueSql: "now()")
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_privacy_rule_entity", x => x.Id);
                });

            migrationBuilder.CreateIndex(
                name: "idx_privacy_rule_owner_key_scope",
                table: "privacy_rule_entity",
                columns: new[] { "UserId", "Key", "ScopeSpaceId" },
                unique: true);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropTable(
                name: "privacy_rule_entity");
        }
    }
}
