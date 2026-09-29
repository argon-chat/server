using Argon.Entities;
using Microsoft.EntityFrameworkCore.Infrastructure;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace Argon.Core.Migrations
{
    /// <summary>
    /// Every live file gets the object row that <c>DropFilesS3Key</c> then makes the only home of its
    /// key: finalized ones with one link, pending ones with none (their ticket's expiry sweep deletes
    /// the object through the row). Soft-deleted files are left alone — their objects are gone.
    /// </summary>
    /// <remarks>
    /// A migration of its own, data only: CockroachDB will not touch a table's rows in the transaction
    /// that changes its schema, so the column goes in the next one. Idempotent, and it steps around an
    /// object already recorded under the same key.
    /// </remarks>
    [DbContext(typeof(ApplicationDbContext))]
    [Migration("20260928233500_BackfillBlobs")]
    public partial class BackfillBlobs : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.Sql("""
                INSERT INTO "Blobs" ("Id", "S3Key", "Size", "ContentType", "Md5", "Sha256", "ClaimedSha256", "Links", "Dedupable",
                                     "VerifyRequestedAt", "CanonicalId", "DeleteAfter", "CreatedAt", "UpdatedAt", "DeletedAt", "IsDeleted")
                SELECT gen_random_uuid(),
                       f."S3Key",
                       f."FileSize",
                       CASE WHEN f."ContentType" IS NULL OR btrim(f."ContentType") = '' THEN 'application/octet-stream'
                            ELSE lower(btrim(split_part(f."ContentType", ';', 1))) END,
                       CASE WHEN btrim(f."Checksum", '"') ~ '^[0-9a-f]{32}$' THEN decode(btrim(f."Checksum", '"'), 'hex') END,
                       NULL,
                       NULL,
                       CASE WHEN f."Finalized" THEN 1 ELSE 0 END,
                       f."Purpose" IN (2, 5, 6, 8, 9),
                       NULL,
                       NULL,
                       NULL,
                       f."CreatedAt",
                       now(),
                       NULL,
                       false
                FROM "Files" f
                WHERE f."BlobId" IS NULL
                  AND f."IsDeleted" = false
                  AND NOT EXISTS (SELECT 1 FROM "Blobs" b WHERE b."S3Key" = f."S3Key");
                """);

            migrationBuilder.Sql("""
                UPDATE "Files" f
                SET "BlobId" = b."Id"
                FROM "Blobs" b
                WHERE f."BlobId" IS NULL
                  AND f."IsDeleted" = false
                  AND b."S3Key" = f."S3Key";
                """);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            // Nothing to take back: the rows describe objects that exist either way.
        }
    }
}
