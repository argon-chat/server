using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace Argon.Core.Migrations
{
    /// <inheritdoc />
    public partial class BlobClaims : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<byte[]>(
                name: "ClaimedSha256",
                table: "Blobs",
                type: "bytea",
                nullable: true);

            migrationBuilder.CreateIndex(
                name: "IX_Blobs_ClaimedSha256_ContentType",
                table: "Blobs",
                columns: new[] { "ClaimedSha256", "ContentType" });
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropIndex(
                name: "IX_Blobs_ClaimedSha256_ContentType",
                table: "Blobs");

            migrationBuilder.DropColumn(
                name: "ClaimedSha256",
                table: "Blobs");
        }
    }
}
