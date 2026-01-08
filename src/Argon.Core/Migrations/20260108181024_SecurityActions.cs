using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace Argon.Core.Migrations
{
    /// <inheritdoc />
    public partial class SecurityActions : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropTable(
                name: "direct_messages");

            migrationBuilder.DropTable(
                name: "user_chats");

            migrationBuilder.CreateTable(
                name: "AutoDeleteSettings",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "uuid", nullable: false),
                    UserId = table.Column<Guid>(type: "uuid", nullable: false),
                    Months = table.Column<int>(type: "integer", nullable: true),
                    Enabled = table.Column<bool>(type: "boolean", nullable: false),
                    CreatedAt = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false),
                    UpdatedAt = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false),
                    DeletedAt = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: true),
                    IsDeleted = table.Column<bool>(type: "boolean", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_AutoDeleteSettings", x => x.Id);
                    table.ForeignKey(
                        name: "FK_AutoDeleteSettings_Users_UserId",
                        column: x => x.UserId,
                        principalTable: "Users",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Cascade);
                });

            migrationBuilder.CreateTable(
                name: "Passkeys",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "uuid", nullable: false),
                    UserId = table.Column<Guid>(type: "uuid", nullable: false),
                    Name = table.Column<string>(type: "text", maxLength: 128, nullable: false),
                    PublicKey = table.Column<string>(type: "text", maxLength: 1024, nullable: true),
                    Challenge = table.Column<string>(type: "text", maxLength: 512, nullable: true),
                    LastUsedAt = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: true),
                    IsCompleted = table.Column<bool>(type: "boolean", nullable: false),
                    CreatedAt = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false),
                    UpdatedAt = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false),
                    DeletedAt = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: true),
                    IsDeleted = table.Column<bool>(type: "boolean", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_Passkeys", x => x.Id);
                    table.ForeignKey(
                        name: "FK_Passkeys_Users_UserId",
                        column: x => x.UserId,
                        principalTable: "Users",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Cascade);
                });

            migrationBuilder.CreateTable(
                name: "PendingEmailChanges",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "uuid", nullable: false),
                    UserId = table.Column<Guid>(type: "uuid", nullable: false),
                    NewEmail = table.Column<string>(type: "text", maxLength: 255, nullable: false),
                    CodeHash = table.Column<string>(type: "text", maxLength: 512, nullable: false),
                    CodeSalt = table.Column<string>(type: "text", maxLength: 128, nullable: false),
                    ExpiresAt = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false),
                    AttemptsLeft = table.Column<int>(type: "integer", nullable: false),
                    CreatedAt = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false),
                    UpdatedAt = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false),
                    DeletedAt = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: true),
                    IsDeleted = table.Column<bool>(type: "boolean", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_PendingEmailChanges", x => x.Id);
                    table.ForeignKey(
                        name: "FK_PendingEmailChanges_Users_UserId",
                        column: x => x.UserId,
                        principalTable: "Users",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Cascade);
                });

            migrationBuilder.CreateTable(
                name: "PendingPhoneChanges",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "uuid", nullable: false),
                    UserId = table.Column<Guid>(type: "uuid", nullable: false),
                    NewPhone = table.Column<string>(type: "text", maxLength: 32, nullable: false),
                    CodeHash = table.Column<string>(type: "text", maxLength: 512, nullable: false),
                    CodeSalt = table.Column<string>(type: "text", maxLength: 128, nullable: false),
                    ExpiresAt = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false),
                    AttemptsLeft = table.Column<int>(type: "integer", nullable: false),
                    CreatedAt = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false),
                    UpdatedAt = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false),
                    DeletedAt = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: true),
                    IsDeleted = table.Column<bool>(type: "boolean", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_PendingPhoneChanges", x => x.Id);
                    table.ForeignKey(
                        name: "FK_PendingPhoneChanges_Users_UserId",
                        column: x => x.UserId,
                        principalTable: "Users",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Cascade);
                });

            migrationBuilder.CreateIndex(
                name: "IX_AutoDeleteSettings_UserId",
                table: "AutoDeleteSettings",
                column: "UserId",
                unique: true);

            migrationBuilder.CreateIndex(
                name: "IX_Passkeys_UserId",
                table: "Passkeys",
                column: "UserId");

            migrationBuilder.CreateIndex(
                name: "IX_PendingEmailChanges_ExpiresAt",
                table: "PendingEmailChanges",
                column: "ExpiresAt");

            migrationBuilder.CreateIndex(
                name: "IX_PendingEmailChanges_UserId",
                table: "PendingEmailChanges",
                column: "UserId");

            migrationBuilder.CreateIndex(
                name: "IX_PendingPhoneChanges_ExpiresAt",
                table: "PendingPhoneChanges",
                column: "ExpiresAt");

            migrationBuilder.CreateIndex(
                name: "IX_PendingPhoneChanges_UserId",
                table: "PendingPhoneChanges",
                column: "UserId");
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropTable(
                name: "AutoDeleteSettings");

            migrationBuilder.DropTable(
                name: "Passkeys");

            migrationBuilder.DropTable(
                name: "PendingEmailChanges");

            migrationBuilder.DropTable(
                name: "PendingPhoneChanges");

            migrationBuilder.CreateTable(
                name: "direct_messages",
                columns: table => new
                {
                    SenderId = table.Column<Guid>(type: "uuid", nullable: false),
                    ReceiverId = table.Column<Guid>(type: "uuid", nullable: false),
                    MessageId = table.Column<long>(type: "BIGINT", nullable: false, defaultValueSql: "unique_rowid()"),
                    CreatedAt = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false),
                    CreatorId = table.Column<Guid>(type: "uuid", nullable: false),
                    DeletedAt = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: true),
                    Entities = table.Column<string>(type: "jsonb", nullable: false),
                    IsDeleted = table.Column<bool>(type: "boolean", nullable: false),
                    ReplyTo = table.Column<long>(type: "BIGINT", nullable: true),
                    Text = table.Column<string>(type: "text", maxLength: 4096, nullable: false),
                    UpdatedAt = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_direct_messages", x => new { x.SenderId, x.ReceiverId, x.MessageId });
                });

            migrationBuilder.CreateTable(
                name: "user_chats",
                columns: table => new
                {
                    UserId = table.Column<Guid>(type: "uuid", nullable: false),
                    PeerId = table.Column<Guid>(type: "uuid", nullable: false),
                    IsPinned = table.Column<bool>(type: "boolean", nullable: false, defaultValue: false),
                    LastMessageAt = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false),
                    LastMessageText = table.Column<string>(type: "text", maxLength: 2048, nullable: true),
                    PinnedAt = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: true),
                    UnreadCount = table.Column<int>(type: "integer", nullable: false, defaultValue: 0)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_user_chats", x => new { x.UserId, x.PeerId });
                });

            migrationBuilder.CreateIndex(
                name: "IX_direct_messages_CreatorId",
                table: "direct_messages",
                column: "CreatorId");

            migrationBuilder.CreateIndex(
                name: "ix_dm_conversation_time",
                table: "direct_messages",
                columns: new[] { "SenderId", "ReceiverId", "CreatedAt" })
                .Annotation("Npgsql:IndexInclude", new[] { "Text", "Entities" });

            migrationBuilder.CreateIndex(
                name: "ix_user_chats_sort",
                table: "user_chats",
                columns: new[] { "UserId", "IsPinned", "PinnedAt", "LastMessageAt" },
                descending: new[] { false, true, true, true });
        }
    }
}
