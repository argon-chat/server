using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace Argon.Core.Migrations
{
    /// <inheritdoc />
    public partial class TenantDirectory : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.CreateTable(
                name: "TenantDirectory",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "uuid", nullable: false),
                    Domain = table.Column<string>(type: "varchar(253)", maxLength: 253, nullable: false),
                    InstanceUrl = table.Column<string>(type: "text", maxLength: 512, nullable: false),
                    IsVerified = table.Column<bool>(type: "boolean", nullable: false),
                    OrgName = table.Column<string>(type: "text", maxLength: 256, nullable: true),
                    OwnerUserId = table.Column<Guid>(type: "uuid", nullable: true),
                    Notes = table.Column<string>(type: "text", maxLength: 1024, nullable: true),
                    CreatedAt = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false),
                    UpdatedAt = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false),
                    DeletedAt = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: true),
                    IsDeleted = table.Column<bool>(type: "boolean", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_TenantDirectory", x => x.Id);
                });

            migrationBuilder.CreateIndex(
                name: "IX_TenantDirectory_Domain",
                table: "TenantDirectory",
                column: "Domain",
                unique: true);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropTable(
                name: "TenantDirectory");
        }
    }
}
