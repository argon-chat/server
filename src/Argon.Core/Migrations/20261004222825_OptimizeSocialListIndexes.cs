using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace Argon.Core.Migrations
{
    /// <inheritdoc />
    public partial class OptimizeSocialListIndexes : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropIndex(
                name: "idx_friend_requests_target",
                table: "user_friend_requests");

            migrationBuilder.CreateIndex(
                name: "idx_friend_requests_requester_requested",
                table: "user_friend_requests",
                columns: new[] { "RequesterId", "RequestedAt", "TargetId" },
                descending: new[] { false, true, false });

            migrationBuilder.CreateIndex(
                name: "idx_friend_requests_target_requested",
                table: "user_friend_requests",
                columns: new[] { "TargetId", "RequestedAt", "RequesterId" },
                descending: new[] { false, true, false });

            migrationBuilder.CreateIndex(
                name: "idx_friendships_user_created",
                table: "friendship_entity",
                columns: new[] { "UserId", "CreatedAt", "FriendId" });
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropIndex(
                name: "idx_friend_requests_requester_requested",
                table: "user_friend_requests");

            migrationBuilder.DropIndex(
                name: "idx_friend_requests_target_requested",
                table: "user_friend_requests");

            migrationBuilder.DropIndex(
                name: "idx_friendships_user_created",
                table: "friendship_entity");

            migrationBuilder.CreateIndex(
                name: "idx_friend_requests_target",
                table: "user_friend_requests",
                column: "TargetId");
        }
    }
}
