using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace Argon.Core.Migrations
{
    /// <inheritdoc />
    public partial class OperatorCertificates : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            // 1. Create the child table first so existing certificates can be migrated into it.
            migrationBuilder.CreateTable(
                name: "OperatorCertificates",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "uuid", nullable: false),
                    OperatorId = table.Column<Guid>(type: "uuid", nullable: false),
                    SerialNumber = table.Column<string>(type: "varchar(128)", maxLength: 128, nullable: false),
                    Thumbprint = table.Column<string>(type: "varchar(128)", maxLength: 128, nullable: false),
                    Subject = table.Column<string>(type: "text", maxLength: 512, nullable: false),
                    NotBefore = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false),
                    NotAfter = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false),
                    DeviceName = table.Column<string>(type: "text", maxLength: 256, nullable: true),
                    DeviceSerialNumber = table.Column<string>(type: "text", maxLength: 128, nullable: true),
                    RevokedAt = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: true),
                    CreatedAt = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false),
                    UpdatedAt = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false),
                    DeletedAt = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: true),
                    IsDeleted = table.Column<bool>(type: "boolean", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_OperatorCertificates", x => x.Id);
                    table.ForeignKey(
                        name: "FK_OperatorCertificates_Operators_OperatorId",
                        column: x => x.OperatorId,
                        principalTable: "Operators",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Cascade);
                });

            migrationBuilder.CreateIndex(
                name: "IX_OperatorCertificates_OperatorId",
                table: "OperatorCertificates",
                column: "OperatorId");

            migrationBuilder.CreateIndex(
                name: "IX_OperatorCertificates_SerialNumber",
                table: "OperatorCertificates",
                column: "SerialNumber");

            migrationBuilder.CreateIndex(
                name: "IX_OperatorCertificates_Thumbprint",
                table: "OperatorCertificates",
                column: "Thumbprint");

            // 2. Migrate existing single-certificate data into the child table as active certificates.
            migrationBuilder.Sql(
                """
                INSERT INTO "OperatorCertificates"
                    ("Id", "OperatorId", "SerialNumber", "Thumbprint", "Subject",
                     "NotBefore", "NotAfter", "DeviceName", "DeviceSerialNumber",
                     "RevokedAt", "CreatedAt", "UpdatedAt", "DeletedAt", "IsDeleted")
                SELECT
                    gen_random_uuid(),
                    o."Id",
                    o."CertificateSerialNumber",
                    o."CertificateThumbprint",
                    COALESCE(o."CertificateSubject", ''),
                    COALESCE(o."CertificateNotBefore", now()),
                    COALESCE(o."CertificateNotAfter", now()),
                    NULL, NULL, NULL,
                    now(), now(), NULL, false
                FROM "Operators" o
                WHERE o."CertificateThumbprint" IS NOT NULL
                  AND o."CertificateSerialNumber" IS NOT NULL
                  AND o."IsDeleted" = false;
                """);

            // 3. Drop the now-migrated columns from the parent table.
            migrationBuilder.DropIndex(
                name: "IX_Operators_CertificateSerialNumber",
                table: "Operators");

            migrationBuilder.DropIndex(
                name: "IX_Operators_CertificateThumbprint",
                table: "Operators");

            migrationBuilder.DropColumn(
                name: "CertificateNotAfter",
                table: "Operators");

            migrationBuilder.DropColumn(
                name: "CertificateNotBefore",
                table: "Operators");

            migrationBuilder.DropColumn(
                name: "CertificateSerialNumber",
                table: "Operators");

            migrationBuilder.DropColumn(
                name: "CertificateSubject",
                table: "Operators");

            migrationBuilder.DropColumn(
                name: "CertificateThumbprint",
                table: "Operators");
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<DateTimeOffset>(
                name: "CertificateNotAfter",
                table: "Operators",
                type: "timestamp with time zone",
                nullable: true);

            migrationBuilder.AddColumn<DateTimeOffset>(
                name: "CertificateNotBefore",
                table: "Operators",
                type: "timestamp with time zone",
                nullable: true);

            migrationBuilder.AddColumn<string>(
                name: "CertificateSerialNumber",
                table: "Operators",
                type: "varchar(128)",
                maxLength: 128,
                nullable: true);

            migrationBuilder.AddColumn<string>(
                name: "CertificateSubject",
                table: "Operators",
                type: "text",
                maxLength: 512,
                nullable: true);

            migrationBuilder.AddColumn<string>(
                name: "CertificateThumbprint",
                table: "Operators",
                type: "varchar(128)",
                maxLength: 128,
                nullable: true);

            // Restore the most recently enrolled active certificate per operator back onto the parent table.
            migrationBuilder.Sql(
                """
                UPDATE "Operators" o
                SET "CertificateSerialNumber" = c."SerialNumber",
                    "CertificateThumbprint"   = c."Thumbprint",
                    "CertificateSubject"      = c."Subject",
                    "CertificateNotBefore"    = c."NotBefore",
                    "CertificateNotAfter"     = c."NotAfter"
                FROM (
                    SELECT DISTINCT ON ("OperatorId")
                        "OperatorId", "SerialNumber", "Thumbprint", "Subject", "NotBefore", "NotAfter"
                    FROM "OperatorCertificates"
                    WHERE "RevokedAt" IS NULL AND "IsDeleted" = false
                    ORDER BY "OperatorId", "CreatedAt" DESC
                ) c
                WHERE o."Id" = c."OperatorId";
                """);

            migrationBuilder.DropTable(
                name: "OperatorCertificates");

            migrationBuilder.CreateIndex(
                name: "IX_Operators_CertificateSerialNumber",
                table: "Operators",
                column: "CertificateSerialNumber");

            migrationBuilder.CreateIndex(
                name: "IX_Operators_CertificateThumbprint",
                table: "Operators",
                column: "CertificateThumbprint");
        }
    }
}
