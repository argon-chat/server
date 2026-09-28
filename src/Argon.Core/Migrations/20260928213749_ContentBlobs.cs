using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace Argon.Core.Migrations
{
    /// <inheritdoc />
    public partial class ContentBlobs : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropIndex(
                name: "IX_Files_S3Key",
                table: "Files");

            migrationBuilder.AddColumn<Guid>(
                name: "BlobId",
                table: "Files",
                type: "uuid",
                nullable: true);

            migrationBuilder.AddColumn<byte[]>(
                name: "ClaimedSha256",
                table: "FileBlobs",
                type: "bytea",
                nullable: true);

            migrationBuilder.CreateTable(
                name: "Blobs",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "uuid", nullable: false),
                    S3Key = table.Column<string>(type: "varchar(512)", maxLength: 512, nullable: false),
                    Size = table.Column<long>(type: "bigint", nullable: false),
                    ContentType = table.Column<string>(type: "varchar(255)", maxLength: 255, nullable: true),
                    Md5 = table.Column<byte[]>(type: "bytea", nullable: true),
                    Sha256 = table.Column<byte[]>(type: "bytea", nullable: true),
                    Links = table.Column<long>(type: "bigint", nullable: false),
                    Dedupable = table.Column<bool>(type: "boolean", nullable: false),
                    VerifyRequestedAt = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: true),
                    CanonicalId = table.Column<Guid>(type: "uuid", nullable: true),
                    DeleteAfter = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: true),
                    CreatedAt = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false),
                    UpdatedAt = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false),
                    DeletedAt = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: true),
                    IsDeleted = table.Column<bool>(type: "boolean", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_Blobs", x => x.Id);
                })
                .Annotation("Cockroach:HashShardedKey", true);

            migrationBuilder.CreateIndex(
                name: "IX_Files_BlobId",
                table: "Files",
                column: "BlobId");

            migrationBuilder.CreateIndex(
                name: "IX_Files_S3Key_Mirror",
                table: "Files",
                column: "S3Key");

            migrationBuilder.CreateIndex(
                name: "IX_Blobs_DeleteAfter",
                table: "Blobs",
                column: "DeleteAfter",
                filter: "\"DeleteAfter\" IS NOT NULL AND \"IsDeleted\" = false");

            migrationBuilder.CreateIndex(
                name: "IX_Blobs_Md5_Size_ContentType",
                table: "Blobs",
                columns: new[] { "Md5", "Size", "ContentType" });

            migrationBuilder.CreateIndex(
                name: "IX_Blobs_S3Key",
                table: "Blobs",
                column: "S3Key",
                unique: true);

            migrationBuilder.CreateIndex(
                name: "IX_Blobs_Sha256_ContentType",
                table: "Blobs",
                columns: new[] { "Sha256", "ContentType" },
                unique: true,
                filter: "\"Sha256\" IS NOT NULL AND \"CanonicalId\" IS NULL AND \"IsDeleted\" = false");

            migrationBuilder.CreateIndex(
                name: "IX_Blobs_VerifyRequestedAt",
                table: "Blobs",
                column: "VerifyRequestedAt",
                filter: "\"VerifyRequestedAt\" IS NOT NULL AND \"IsDeleted\" = false");
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropTable(
                name: "Blobs");

            migrationBuilder.DropIndex(
                name: "IX_Files_BlobId",
                table: "Files");

            migrationBuilder.DropIndex(
                name: "IX_Files_S3Key_Mirror",
                table: "Files");

            migrationBuilder.DropColumn(
                name: "BlobId",
                table: "Files");

            migrationBuilder.DropColumn(
                name: "ClaimedSha256",
                table: "FileBlobs");

            migrationBuilder.CreateIndex(
                name: "IX_Files_S3Key",
                table: "Files",
                column: "S3Key",
                unique: true);
        }
    }
}
