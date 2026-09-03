using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace Argon.Core.Migrations
{
    /// <inheritdoc />
    public partial class FileBlobsExpiresAtPartialIndex : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropIndex(
                name: "IX_FileBlobs_ExpiresAt",
                table: "FileBlobs");

            migrationBuilder.CreateIndex(
                name: "IX_FileBlobs_ExpiresAt",
                table: "FileBlobs",
                column: "ExpiresAt",
                filter: "\"IsDeleted\" = false")
                .Annotation("Npgsql:CreatedConcurrently", true);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropIndex(
                name: "IX_FileBlobs_ExpiresAt",
                table: "FileBlobs");

            migrationBuilder.CreateIndex(
                name: "IX_FileBlobs_ExpiresAt",
                table: "FileBlobs",
                column: "ExpiresAt");
        }
    }
}
