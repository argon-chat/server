using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace Argon.Api.Migrations.Clickhouse
{
    /// <inheritdoc />
    public partial class AddedClickhouseContext : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.Sql("USE argen");

            migrationBuilder.Sql(@"
                CREATE TABLE Messages
                (
                    Id UUID,
                    ChannelId UUID,
                    ReplyToMessage UUID,
                    Text String,
                    CreatedAt DateTime,
                    UpdatedAt DateTime,
                    DeletedAt DateTime,
                    IsDeleted Bool,
                    CreatorId UUID,
                    PRIMARY KEY (Id)
                ) ENGINE = MergeTree()
                ORDER BY (Id, CreatorId)");

            migrationBuilder.Sql(@"
                CREATE TABLE Documents
                (
                    Id UUID,
                    MessageId UUID,
                    FileName String,
                    MimeType String,
                    FileSize UInt64,
                    FileId String,
                    CreatedAt DateTime,
                    UpdatedAt DateTime,
                    DeletedAt DateTime,
                    IsDeleted Bool,
                    CreatorId UUID,
                    PRIMARY KEY (Id),
                    FOREIGN KEY (MessageId) REFERENCES Messages(Id)
                ) ENGINE = MergeTree()
                ORDER BY (Id, CreatorId)");

            migrationBuilder.Sql(@"
                CREATE TABLE Entities
                (
                    Id UUID,
                    MessageId UUID,
                    Type UInt16,
                    Offset Int32,
                    Length Int32,
                    UrlMask String,
                    CreatedAt DateTime,
                    UpdatedAt DateTime,
                    DeletedAt DateTime,
                    IsDeleted Bool,
                    CreatorId UUID,
                    PRIMARY KEY (Id),
                    FOREIGN KEY (MessageId) REFERENCES Messages(Id)
                ) ENGINE = MergeTree()
                ORDER BY (Id, CreatorId)");

            migrationBuilder.Sql(@"
                CREATE TABLE Images
                (
                    Id UUID,
                    MessageId UUID,
                    FileName String,
                    MimeType String,
                    IsVideo Bool,
                    FileId String,
                    Width Int32,
                    Height Int32,
                    FileSize UInt64,
                    CreatedAt DateTime,
                    UpdatedAt DateTime,
                    DeletedAt DateTime,
                    IsDeleted Bool,
                    CreatorId UUID,
                    PRIMARY KEY (Id),
                    FOREIGN KEY (MessageId) REFERENCES Messages(Id)
                ) ENGINE = MergeTree()
                ORDER BY (Id, CreatorId)");

            migrationBuilder.Sql(@"
                CREATE TABLE Stickers
                (
                    Id UUID,
                    MessageId UUID,
                    IsAnimated Bool,
                    Emoji String,
                    FileId String,
                    CreatedAt DateTime,
                    UpdatedAt DateTime,
                    DeletedAt DateTime,
                    IsDeleted Bool,
                    CreatorId UUID,
                    PRIMARY KEY (Id),
                    FOREIGN KEY (MessageId) REFERENCES Messages(Id)
                ) ENGINE = MergeTree()
                ORDER BY (Id, CreatorId)");
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropTable(
                name: "Documents");

            migrationBuilder.DropTable(
                name: "Entities");

            migrationBuilder.DropTable(
                name: "Images");

            migrationBuilder.DropTable(
                name: "Stickers");

            migrationBuilder.DropTable(
                name: "Messages");
        }
    }
}