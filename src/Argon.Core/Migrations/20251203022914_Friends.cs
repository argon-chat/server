using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace Argon.Core.Migrations
{
    /// <inheritdoc />
    public partial class Friends : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.CreateTable(
                name: "friendship_entity",
                columns: table => new
                {
                    UserId = table.Column<Guid>(type: "uuid", nullable: false),
                    FriendId = table.Column<Guid>(type: "uuid", nullable: false),
                    CreatedAt = table.Column<DateTimeOffset>(type: "timestamptz", nullable: false, defaultValueSql: "now()")
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_friendship_entity", x => new { x.UserId, x.FriendId });
                });

            migrationBuilder.CreateTable(
                name: "user_blocks",
                columns: table => new
                {
                    UserId = table.Column<Guid>(type: "uuid", nullable: false),
                    BlockedId = table.Column<Guid>(type: "uuid", nullable: false),
                    CreatedAt = table.Column<DateTimeOffset>(type: "timestamptz", nullable: false, defaultValueSql: "now()")
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_user_blocks", x => new { x.UserId, x.BlockedId });
                });

            migrationBuilder.CreateTable(
                name: "user_friend_requests",
                columns: table => new
                {
                    RequesterId = table.Column<Guid>(type: "uuid", nullable: false),
                    TargetId = table.Column<Guid>(type: "uuid", nullable: false),
                    RequestedAt = table.Column<DateTimeOffset>(type: "timestamptz", nullable: false, defaultValueSql: "now()"),
                    ExpiredAt = table.Column<DateTime>(type: "timestamptz", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_user_friend_requests", x => new { x.RequesterId, x.TargetId });
                });

            migrationBuilder.InsertData(
                table: "Users",
                columns: new[] { "Id", "AgreeTOS", "AllowedSendOptionalEmails", "AvatarFileId", "CreatedAt", "DateOfBirth", "DeletedAt", "DisplayName", "Email", "IsDeleted", "LockDownExpiration", "LockDownIsAppealable", "LockdownReason", "PasswordDigest", "PhoneNumber", "PreferredAuthMode", "PreferredOtpMethod", "TotpSecret", "UpdatedAt", "Username" },
                values: new object[] { new Guid("44444444-2222-1111-2222-444444444444"), false, false, null, new DateTimeOffset(new DateTime(1, 1, 1, 0, 0, 0, 0, DateTimeKind.Unspecified), new TimeSpan(0, 0, 0, 0, 0)), null, null, "Echo", "echo@argon.gl", false, null, false, 0, null, null, 0, 0, null, new DateTimeOffset(new DateTime(1, 1, 1, 0, 0, 0, 0, DateTimeKind.Unspecified), new TimeSpan(0, 0, 0, 0, 0)), "echo" });

            migrationBuilder.CreateIndex(
                name: "idx_friendships_user",
                table: "friendship_entity",
                column: "UserId");

            migrationBuilder.CreateIndex(
                name: "idx_user_blocks_user",
                table: "user_blocks",
                column: "UserId");

            migrationBuilder.CreateIndex(
                name: "idx_friend_requests_requester",
                table: "user_friend_requests",
                column: "RequesterId");

            migrationBuilder.CreateIndex(
                name: "idx_friend_requests_target",
                table: "user_friend_requests",
                column: "TargetId");
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropTable(
                name: "friendship_entity");

            migrationBuilder.DropTable(
                name: "user_blocks");

            migrationBuilder.DropTable(
                name: "user_friend_requests");

            migrationBuilder.DeleteData(
                table: "Users",
                keyColumn: "Id",
                keyValue: new Guid("44444444-2222-1111-2222-444444444444"));
        }
    }
}
