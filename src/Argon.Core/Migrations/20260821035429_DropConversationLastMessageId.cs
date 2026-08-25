using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace Argon.Core.Migrations
{
    /// <inheritdoc />
    public partial class DropConversationLastMessageId : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            // The scaffolder warns that this may lose data, and it does: the column holds a value for
            // every conversation with a message in it. Losing it is the point. Nothing in the product
            // ever read it — grep across src/ and tests/ finds the declaration and one assignment and
            // nothing else — while the assignment sat inside the direct-message send transaction,
            // beside the two user_conversations rows it also writes. Unread for direct messages runs
            // off UserConversations.UnreadCount, which is a real per-user counter.
            //
            // Down restores the column but not the values, which is the honest shape for a write-only
            // field: there is nowhere to restore them from and nothing that would notice.
            migrationBuilder.DropColumn(
                name: "LastMessageId",
                table: "conversations");
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<long>(
                name: "LastMessageId",
                table: "conversations",
                type: "bigint",
                nullable: false,
                defaultValue: 0L);
        }
    }
}
