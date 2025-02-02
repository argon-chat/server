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
                    FileId UUID,
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
                    FileId UUID,
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
                    FileId UUID,
                    CreatedAt DateTime,
                    UpdatedAt DateTime,
                    DeletedAt DateTime,
                    IsDeleted Bool,
                    CreatorId UUID,
                    PRIMARY KEY (Id),
                    FOREIGN KEY (MessageId) REFERENCES Messages(Id)
                ) ENGINE = MergeTree()
                ORDER BY (Id, CreatorId)");


            // migrationBuilder.CreateTable(
            //     name: "Messages",
            //     columns: table => new
            //     {
            //         Id             = table.Column<Guid>(type: "UUID", nullable: false),
            //         ChannelId      = table.Column<Guid>(type: "UUID", nullable: false),
            //         ReplyToMessage = table.Column<Guid>(type: "UUID", nullable: false),
            //         Text           = table.Column<string>(type: "String", maxLength: 1024, nullable: false),
            //         CreatedAt      = table.Column<DateTime>(type: "DateTime", nullable: false),
            //         UpdatedAt      = table.Column<DateTime>(type: "DateTime", nullable: false),
            //         DeletedAt      = table.Column<DateTime>(type: "DateTime", nullable: true),
            //         IsDeleted      = table.Column<bool>(type: "Bool", nullable: false),
            //         CreatorId      = table.Column<Guid>(type: "UUID", nullable: false)
            //     },
            //     constraints: table => { table.PrimaryKey("PK_Messages", x => x.Id); });
            //
            // migrationBuilder.CreateTable(
            //     name: "Documents",
            //     columns: table => new
            //     {
            //         Id        = table.Column<Guid>(type: "UUID", nullable: false),
            //         MessageId = table.Column<Guid>(type: "UUID", nullable: false),
            //         FileName  = table.Column<string>(type: "String", nullable: false),
            //         MimeType  = table.Column<string>(type: "String", nullable: false),
            //         FileSize  = table.Column<ulong>(type: "UInt64", nullable: false),
            //         FileId    = table.Column<Guid>(type: "UUID", nullable: false),
            //         CreatedAt = table.Column<DateTime>(type: "DateTime", nullable: false),
            //         UpdatedAt = table.Column<DateTime>(type: "DateTime", nullable: false),
            //         DeletedAt = table.Column<DateTime>(type: "DateTime", nullable: true),
            //         IsDeleted = table.Column<bool>(type: "Bool", nullable: false),
            //         CreatorId = table.Column<Guid>(type: "UUID", nullable: false)
            //     },
            //     constraints: table =>
            //     {
            //         table.PrimaryKey("PK_Documents", x => x.Id);
            //         table.ForeignKey(
            //             name: "FK_Documents_Messages_MessageId",
            //             column: x => x.MessageId,
            //             principalTable: "Messages",
            //             principalColumn: "Id");
            //     });
            //
            // migrationBuilder.CreateTable(
            //     name: "Entities",
            //     columns: table => new
            //     {
            //         Id        = table.Column<Guid>(type: "UUID", nullable: false),
            //         MessageId = table.Column<Guid>(type: "UUID", nullable: false),
            //         Type      = table.Column<ushort>(type: "UInt16", nullable: false),
            //         Offset    = table.Column<int>(type: "Int32", nullable: false),
            //         Length    = table.Column<int>(type: "Int32", nullable: false),
            //         UrlMask   = table.Column<string>(type: "String", nullable: true),
            //         CreatedAt = table.Column<DateTime>(type: "DateTime", nullable: false),
            //         UpdatedAt = table.Column<DateTime>(type: "DateTime", nullable: false),
            //         DeletedAt = table.Column<DateTime>(type: "DateTime", nullable: true),
            //         IsDeleted = table.Column<bool>(type: "Bool", nullable: false),
            //         CreatorId = table.Column<Guid>(type: "UUID", nullable: false)
            //     },
            //     constraints: table =>
            //     {
            //         table.PrimaryKey("PK_Entities", x => x.Id);
            //         table.ForeignKey(
            //             name: "FK_Entities_Messages_MessageId",
            //             column: x => x.MessageId,
            //             principalTable: "Messages",
            //             principalColumn: "Id");
            //     });
            //
            // migrationBuilder.CreateTable(
            //     name: "Images",
            //     columns: table => new
            //     {
            //         Id        = table.Column<Guid>(type: "UUID", nullable: false),
            //         MessageId = table.Column<Guid>(type: "UUID", nullable: false),
            //         FileName  = table.Column<string>(type: "String", nullable: false),
            //         MimeType  = table.Column<string>(type: "String", nullable: false),
            //         IsVideo   = table.Column<bool>(type: "Bool", nullable: false),
            //         FileId    = table.Column<Guid>(type: "UUID", nullable: false),
            //         Width     = table.Column<int>(type: "Int32", nullable: false),
            //         Height    = table.Column<int>(type: "Int32", nullable: false),
            //         FileSize  = table.Column<ulong>(type: "UInt64", nullable: false),
            //         CreatedAt = table.Column<DateTime>(type: "DateTime", nullable: false),
            //         UpdatedAt = table.Column<DateTime>(type: "DateTime", nullable: false),
            //         DeletedAt = table.Column<DateTime>(type: "DateTime", nullable: true),
            //         IsDeleted = table.Column<bool>(type: "Bool", nullable: false),
            //         CreatorId = table.Column<Guid>(type: "UUID", nullable: false)
            //     },
            //     constraints: table =>
            //     {
            //         table.PrimaryKey("PK_Images", x => x.Id);
            //         table.ForeignKey(
            //             name: "FK_Images_Messages_MessageId",
            //             column: x => x.MessageId,
            //             principalTable: "Messages",
            //             principalColumn: "Id");
            //     });
            //
            // migrationBuilder.CreateTable(
            //     name: "Stickers",
            //     columns: table => new
            //     {
            //         Id         = table.Column<Guid>(type: "UUID", nullable: false),
            //         MessageId  = table.Column<Guid>(type: "UUID", nullable: false),
            //         IsAnimated = table.Column<bool>(type: "Bool", nullable: false),
            //         Emoji      = table.Column<string>(type: "String", nullable: false),
            //         FileId     = table.Column<Guid>(type: "UUID", nullable: false),
            //         CreatedAt  = table.Column<DateTime>(type: "DateTime", nullable: false),
            //         UpdatedAt  = table.Column<DateTime>(type: "DateTime", nullable: false),
            //         DeletedAt  = table.Column<DateTime>(type: "DateTime", nullable: true),
            //         IsDeleted  = table.Column<bool>(type: "Bool", nullable: false),
            //         CreatorId  = table.Column<Guid>(type: "UUID", nullable: false)
            //     },
            //     constraints: table =>
            //     {
            //         table.PrimaryKey("PK_Stickers", x => x.Id);
            //         table.ForeignKey(
            //             name: "FK_Stickers_Messages_MessageId",
            //             column: x => x.MessageId,
            //             principalTable: "Messages",
            //             principalColumn: "Id");
            //     });
            //
            // // migrationBuilder.CreateIndex(
            // //     name: "IX_Documents_CreatorId",
            // //     table: "Documents",
            // //     column: "CreatorId");
            // //
            // // migrationBuilder.CreateIndex(
            // //     name: "IX_Entities_CreatorId",
            // //     table: "Entities",
            // //     column: "CreatorId");
            // //
            // // migrationBuilder.CreateIndex(
            // //     name: "IX_Images_CreatorId",
            // //     table: "Images",
            // //     column: "CreatorId");
            // //
            // // migrationBuilder.CreateIndex(
            // //     name: "IX_Messages_CreatorId",
            // //     table: "Messages",
            // //     column: "CreatorId");
            // //
            // // migrationBuilder.CreateIndex(
            // //     name: "IX_Stickers_CreatorId",
            // //     table: "Stickers",
            // //     column: "CreatorId");
            // migrationBuilder.Sql("CREATE INDEX IX_Documents_CreatorId ON Documents (CreatorId) TYPE hash");
            // migrationBuilder.Sql("CREATE INDEX IX_Entities_CreatorId ON Entities (CreatorId) TYPE hash");
            // migrationBuilder.Sql("CREATE INDEX IX_Images_CreatorId ON Images (CreatorId) TYPE hash");
            // migrationBuilder.Sql("CREATE INDEX IX_Messages_CreatorId ON Messages (CreatorId) TYPE hash");
            // migrationBuilder.Sql("CREATE INDEX IX_Stickers_CreatorId ON Stickers (CreatorId) TYPE hash");
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