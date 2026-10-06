using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace Argon.Core.Migrations
{
    /// <inheritdoc />
    public partial class VideoUploads : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<string>(
                name: "Declaration",
                table: "FileBlobs",
                type: "jsonb",
                nullable: true);

            migrationBuilder.AddColumn<int>(
                name: "PartCount",
                table: "FileBlobs",
                type: "integer",
                nullable: true);

            migrationBuilder.AddColumn<long>(
                name: "PartSize",
                table: "FileBlobs",
                type: "bigint",
                nullable: true);

            migrationBuilder.AddColumn<string>(
                name: "UploadId",
                table: "FileBlobs",
                type: "text",
                maxLength: 256,
                nullable: true);

            migrationBuilder.CreateTable(
                name: "FileMedia",
                columns: table => new
                {
                    FileId = table.Column<Guid>(type: "uuid", nullable: false),
                    Width = table.Column<int>(type: "integer", nullable: false),
                    Height = table.Column<int>(type: "integer", nullable: false),
                    DurationMs = table.Column<int>(type: "integer", nullable: false),
                    HasAudio = table.Column<bool>(type: "boolean", nullable: false),
                    VideoCodec = table.Column<string>(type: "text", maxLength: 64, nullable: true),
                    AudioCodec = table.Column<string>(type: "text", maxLength: 64, nullable: true),
                    PosterFileId = table.Column<Guid>(type: "uuid", nullable: true),
                    StoryboardFileId = table.Column<Guid>(type: "uuid", nullable: true),
                    StoryboardFrameWidth = table.Column<int>(type: "integer", nullable: true),
                    StoryboardFrameHeight = table.Column<int>(type: "integer", nullable: true),
                    StoryboardColumns = table.Column<int>(type: "integer", nullable: true),
                    StoryboardFrameCount = table.Column<int>(type: "integer", nullable: true),
                    StoryboardIntervalMs = table.Column<int>(type: "integer", nullable: true),
                    ThumbHash = table.Column<string>(type: "text", maxLength: 64, nullable: true),
                    PreloadPrefixSize = table.Column<long>(type: "bigint", nullable: true),
                    CreatedAt = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false),
                    UpdatedAt = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false),
                    DeletedAt = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: true),
                    IsDeleted = table.Column<bool>(type: "boolean", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_FileMedia", x => x.FileId);
                    table.ForeignKey(
                        name: "FK_FileMedia_Files_FileId",
                        column: x => x.FileId,
                        principalTable: "Files",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Cascade);
                })
                .Annotation("Cockroach:HashShardedKey", true);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropTable(
                name: "FileMedia");

            migrationBuilder.DropColumn(
                name: "Declaration",
                table: "FileBlobs");

            migrationBuilder.DropColumn(
                name: "PartCount",
                table: "FileBlobs");

            migrationBuilder.DropColumn(
                name: "PartSize",
                table: "FileBlobs");

            migrationBuilder.DropColumn(
                name: "UploadId",
                table: "FileBlobs");
        }
    }
}
