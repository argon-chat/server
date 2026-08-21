using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace Argon.Core.Migrations
{
    /// <inheritdoc />
    public partial class DeviceKeys : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.CreateTable(
                name: "DeviceKeys",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "uuid", nullable: false),
                    DeviceId = table.Column<Guid>(type: "uuid", nullable: false),
                    Thumbprint = table.Column<string>(type: "varchar(64)", maxLength: 64, nullable: false),
                    PublicKey = table.Column<string>(type: "text", nullable: false),
                    Platform = table.Column<int>(type: "integer", nullable: false),
                    Assurance = table.Column<int>(type: "integer", nullable: false),
                    ClientName = table.Column<string>(type: "text", nullable: false),
                    EnrolledAt = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false),
                    LastProvenAt = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: true),
                    CreatedAt = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false),
                    UpdatedAt = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false),
                    DeletedAt = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: true),
                    IsDeleted = table.Column<bool>(type: "boolean", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_DeviceKeys", x => x.Id);
                });

            migrationBuilder.CreateIndex(
                name: "IX_DeviceKeys_DeviceId",
                table: "DeviceKeys",
                column: "DeviceId",
                unique: true);

            migrationBuilder.CreateIndex(
                name: "IX_DeviceKeys_Thumbprint",
                table: "DeviceKeys",
                column: "Thumbprint",
                unique: true);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropTable(
                name: "DeviceKeys");
        }
    }
}
