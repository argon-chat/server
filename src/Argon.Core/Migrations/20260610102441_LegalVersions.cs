using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace Argon.Core.Migrations
{
    /// <inheritdoc />
    public partial class LegalVersions : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            // Idempotent, single schema change. On CockroachDB this migration must
            // survive a partial re-run: the original scaffold added each column in a
            // separate statement and then ran seed UPDATEs (DML) against the brand-new
            // columns in the same transaction — Cockroach committed the first ADD COLUMN,
            // then aborted, leaving "AgreePrivacyVersion" behind and the migration
            // unrecorded. ADD COLUMN IF NOT EXISTS (both columns in one ALTER, no DML)
            // makes re-applying safe whatever state the table is left in.
            migrationBuilder.Sql(
                "ALTER TABLE \"Users\" " +
                "ADD COLUMN IF NOT EXISTS \"AgreeTosVersion\" text, " +
                "ADD COLUMN IF NOT EXISTS \"AgreePrivacyVersion\" text;");
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.Sql(
                "ALTER TABLE \"Users\" " +
                "DROP COLUMN IF EXISTS \"AgreePrivacyVersion\", " +
                "DROP COLUMN IF EXISTS \"AgreeTosVersion\";");
        }
    }
}
