using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace Argon.Core.Migrations
{
    /// <inheritdoc />
    public partial class HashShardingAndIndexAudit : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropIndex(
                name: "idx_user_ignores_user",
                table: "user_ignores");

            migrationBuilder.DropIndex(
                name: "idx_friend_requests_requester",
                table: "user_friend_requests");

            migrationBuilder.DropIndex(
                name: "idx_user_blocks_user",
                table: "user_blocks");

            migrationBuilder.DropIndex(
                name: "idx_friendships_user",
                table: "friendship_entity");

            migrationBuilder.DropIndex(
                name: "ix_conversations_participant1",
                table: "conversations");

            migrationBuilder.DropIndex(
                name: "IX_UserLevels_CanClaimMedal",
                table: "UserLevels");

            migrationBuilder.DropIndex(
                name: "IX_UserLevels_CurrentLevel",
                table: "UserLevels");

            migrationBuilder.DropIndex(
                name: "IX_UserDailyStats_Date",
                table: "UserDailyStats");

            migrationBuilder.DropIndex(
                name: "IX_UserDailyStats_UserId",
                table: "UserDailyStats");

            migrationBuilder.DropIndex(
                name: "IX_UserDailyStats_UserId_Date",
                table: "UserDailyStats");

            migrationBuilder.DropIndex(
                name: "IX_UltimaSubscriptions_Status",
                table: "UltimaSubscriptions");

            migrationBuilder.DropIndex(
                name: "IX_UltimaSubscriptions_UserId",
                table: "UltimaSubscriptions");

            migrationBuilder.DropIndex(
                name: "IX_SpaceBoosts_UserId",
                table: "SpaceBoosts");

            migrationBuilder.DropIndex(
                name: "IX_SavedGifs_UserId",
                table: "SavedGifs");

            migrationBuilder.DropIndex(
                name: "IX_Reports_Category",
                table: "Reports");

            migrationBuilder.DropIndex(
                name: "IX_Reports_CreatedAt",
                table: "Reports");

            migrationBuilder.DropIndex(
                name: "IX_Reports_ReporterId",
                table: "Reports");

            migrationBuilder.DropIndex(
                name: "IX_Reports_Status",
                table: "Reports");

            migrationBuilder.DropIndex(
                name: "IX_Reports_TargetId",
                table: "Reports");

            migrationBuilder.DropIndex(
                name: "idx_reports_per_target",
                table: "Reports");

            migrationBuilder.DropIndex(
                name: "IX_ReportCases_LastReportedAt",
                table: "ReportCases");

            migrationBuilder.DropIndex(
                name: "IX_OperatorAuditLog_CreatedAt",
                table: "OperatorAuditLog");

            migrationBuilder.DropIndex(
                name: "ix_notification_counters_updated_at",
                table: "NotificationCounters");

            migrationBuilder.DropIndex(
                name: "ix_notification_counters_user_id",
                table: "NotificationCounters");

            migrationBuilder.DropIndex(
                name: "IX_Items_Id_IsReference",
                table: "Items");

            migrationBuilder.DropIndex(
                name: "IX_Items_Id_OwnerId",
                table: "Items");

            migrationBuilder.DropIndex(
                name: "IX_Items_IsReference",
                table: "Items");

            migrationBuilder.DropIndex(
                name: "IX_Items_OwnerId",
                table: "Items");

            migrationBuilder.DropIndex(
                name: "IX_Items_TemplateId",
                table: "Items");

            migrationBuilder.DropIndex(
                name: "IX_FileBlobs_ExpiresAt",
                table: "FileBlobs");

            migrationBuilder.DropIndex(
                name: "IX_FileBlobs_FileId",
                table: "FileBlobs");

            migrationBuilder.DropIndex(
                name: "IX_FeatureFlags_DefaultEnabled",
                table: "FeatureFlags");

            migrationBuilder.DropIndex(
                name: "IX_FeatureFlagOverrides_Scope",
                table: "FeatureFlagOverrides");

            migrationBuilder.DropIndex(
                name: "IX_DeviceObservations_UserId",
                table: "DeviceObservations");

            migrationBuilder.DropIndex(
                name: "IX_ContentViolations_CreatedAt",
                table: "ContentViolations");

            migrationBuilder.DropIndex(
                name: "ix_channel_read_states_user",
                table: "ChannelReadStates");

            migrationBuilder.AlterTable(
                name: "SystemNotifications")
                .Annotation("Cockroach:HashShardedKey", true);

            migrationBuilder.AlterTable(
                name: "Reports")
                .Annotation("Cockroach:HashShardedKey", true);

            migrationBuilder.AlterTable(
                name: "OperatorAuditLog")
                .Annotation("Cockroach:HashShardedKey", true);

            migrationBuilder.AlterTable(
                name: "Files")
                .Annotation("Cockroach:HashShardedKey", true);

            migrationBuilder.AlterTable(
                name: "FileCounters")
                .Annotation("Cockroach:HashShardedKey", true);

            migrationBuilder.AlterTable(
                name: "FileBlobs")
                .Annotation("Cockroach:HashShardedKey", true);

            migrationBuilder.AlterTable(
                name: "DeviceObservations")
                .Annotation("Cockroach:HashShardedKey", true);

            migrationBuilder.AddColumn<string>(
                name: "NormalizedName",
                table: "Spaces",
                type: "text",
                nullable: false,
                computedColumnSql: "lower(\"Name\")",
                stored: true);

            migrationBuilder.AddColumn<string>(
                name: "NormalizedName",
                table: "DevTeamEntity",
                type: "text",
                nullable: false,
                computedColumnSql: "lower(\"Name\")",
                stored: true);

            migrationBuilder.AddColumn<string>(
                name: "NormalizedName",
                table: "DevApps",
                type: "text",
                nullable: false,
                computedColumnSql: "lower(\"Name\")",
                stored: true);

            migrationBuilder.CreateIndex(
                name: "idx_user_ignores_ignored",
                table: "user_ignores",
                column: "IgnoredId");

            migrationBuilder.CreateIndex(
                name: "idx_user_blocks_blocked",
                table: "user_blocks",
                column: "BlockedId");

            migrationBuilder.CreateIndex(
                name: "idx_friendships_friend",
                table: "friendship_entity",
                column: "FriendId");

            migrationBuilder.CreateIndex(
                name: "IX_Users_PhoneNumber",
                table: "Users",
                column: "PhoneNumber",
                filter: "\"PhoneNumber\" IS NOT NULL");

            migrationBuilder.CreateIndex(
                name: "IX_UserDailyStats_Date",
                table: "UserDailyStats",
                column: "Date")
                .Annotation("Cockroach:HashSharded", true);

            migrationBuilder.CreateIndex(
                name: "IX_UltimaSubscriptions_UserId_Status",
                table: "UltimaSubscriptions",
                columns: new[] { "UserId", "Status" });

            migrationBuilder.CreateIndex(
                name: "IX_Spaces_NormalizedName",
                table: "Spaces",
                column: "NormalizedName");

            migrationBuilder.CreateIndex(
                name: "IX_SavedGifs_UserId_AddedAt",
                table: "SavedGifs",
                columns: new[] { "UserId", "AddedAt" });

            migrationBuilder.CreateIndex(
                name: "IX_Reports_CreatedAt",
                table: "Reports",
                column: "CreatedAt")
                .Annotation("Cockroach:HashSharded", true);

            migrationBuilder.CreateIndex(
                name: "IX_Reports_TargetId_Status",
                table: "Reports",
                columns: new[] { "TargetId", "Status" });

            migrationBuilder.CreateIndex(
                name: "IX_ReportCases_LastReportedAt",
                table: "ReportCases",
                column: "LastReportedAt")
                .Annotation("Cockroach:HashSharded", true);

            migrationBuilder.CreateIndex(
                name: "IX_OperatorAuditLog_CreatedAt",
                table: "OperatorAuditLog",
                column: "CreatedAt")
                .Annotation("Cockroach:HashSharded", true);

            migrationBuilder.CreateIndex(
                name: "ix_notification_counters_updated_at",
                table: "NotificationCounters",
                column: "UpdatedAt")
                .Annotation("Cockroach:HashSharded", true);

            migrationBuilder.CreateIndex(
                name: "IX_Items_TemplateId",
                table: "Items",
                column: "TemplateId",
                filter: "\"IsReference\" = true");

            migrationBuilder.CreateIndex(
                name: "IX_FileBlobs_ExpiresAt",
                table: "FileBlobs",
                column: "ExpiresAt",
                filter: "\"IsDeleted\" = false")
                .Annotation("Cockroach:HashSharded", true)
                .Annotation("Npgsql:CreatedConcurrently", true);

            migrationBuilder.CreateIndex(
                name: "IX_FileBlobs_FileId",
                table: "FileBlobs",
                column: "FileId")
                .Annotation("Cockroach:HashSharded", true);

            migrationBuilder.CreateIndex(
                name: "IX_DevTeamEntity_NormalizedName",
                table: "DevTeamEntity",
                column: "NormalizedName");

            migrationBuilder.CreateIndex(
                name: "IX_DevApps_NormalizedName",
                table: "DevApps",
                column: "NormalizedName");

            migrationBuilder.CreateIndex(
                name: "IX_ContentViolations_CreatedAt",
                table: "ContentViolations",
                column: "CreatedAt")
                .Annotation("Cockroach:HashSharded", true);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropIndex(
                name: "idx_user_ignores_ignored",
                table: "user_ignores");

            migrationBuilder.DropIndex(
                name: "idx_user_blocks_blocked",
                table: "user_blocks");

            migrationBuilder.DropIndex(
                name: "idx_friendships_friend",
                table: "friendship_entity");

            migrationBuilder.DropIndex(
                name: "IX_Users_PhoneNumber",
                table: "Users");

            migrationBuilder.DropIndex(
                name: "IX_UserDailyStats_Date",
                table: "UserDailyStats");

            migrationBuilder.DropIndex(
                name: "IX_UltimaSubscriptions_UserId_Status",
                table: "UltimaSubscriptions");

            migrationBuilder.DropIndex(
                name: "IX_Spaces_NormalizedName",
                table: "Spaces");

            migrationBuilder.DropIndex(
                name: "IX_SavedGifs_UserId_AddedAt",
                table: "SavedGifs");

            migrationBuilder.DropIndex(
                name: "IX_Reports_CreatedAt",
                table: "Reports");

            migrationBuilder.DropIndex(
                name: "IX_Reports_TargetId_Status",
                table: "Reports");

            migrationBuilder.DropIndex(
                name: "IX_ReportCases_LastReportedAt",
                table: "ReportCases");

            migrationBuilder.DropIndex(
                name: "IX_OperatorAuditLog_CreatedAt",
                table: "OperatorAuditLog");

            migrationBuilder.DropIndex(
                name: "ix_notification_counters_updated_at",
                table: "NotificationCounters");

            migrationBuilder.DropIndex(
                name: "IX_Items_TemplateId",
                table: "Items");

            migrationBuilder.DropIndex(
                name: "IX_FileBlobs_ExpiresAt",
                table: "FileBlobs");

            migrationBuilder.DropIndex(
                name: "IX_FileBlobs_FileId",
                table: "FileBlobs");

            migrationBuilder.DropIndex(
                name: "IX_DevTeamEntity_NormalizedName",
                table: "DevTeamEntity");

            migrationBuilder.DropIndex(
                name: "IX_DevApps_NormalizedName",
                table: "DevApps");

            migrationBuilder.DropIndex(
                name: "IX_ContentViolations_CreatedAt",
                table: "ContentViolations");

            migrationBuilder.DropColumn(
                name: "NormalizedName",
                table: "Spaces");

            migrationBuilder.DropColumn(
                name: "NormalizedName",
                table: "DevTeamEntity");

            migrationBuilder.DropColumn(
                name: "NormalizedName",
                table: "DevApps");

            migrationBuilder.AlterTable(
                name: "SystemNotifications")
                .OldAnnotation("Cockroach:HashShardedKey", true);

            migrationBuilder.AlterTable(
                name: "Reports")
                .OldAnnotation("Cockroach:HashShardedKey", true);

            migrationBuilder.AlterTable(
                name: "OperatorAuditLog")
                .OldAnnotation("Cockroach:HashShardedKey", true);

            migrationBuilder.AlterTable(
                name: "Files")
                .OldAnnotation("Cockroach:HashShardedKey", true);

            migrationBuilder.AlterTable(
                name: "FileCounters")
                .OldAnnotation("Cockroach:HashShardedKey", true);

            migrationBuilder.AlterTable(
                name: "FileBlobs")
                .OldAnnotation("Cockroach:HashShardedKey", true);

            migrationBuilder.AlterTable(
                name: "DeviceObservations")
                .OldAnnotation("Cockroach:HashShardedKey", true);

            migrationBuilder.CreateIndex(
                name: "idx_user_ignores_user",
                table: "user_ignores",
                column: "UserId");

            migrationBuilder.CreateIndex(
                name: "idx_friend_requests_requester",
                table: "user_friend_requests",
                column: "RequesterId");

            migrationBuilder.CreateIndex(
                name: "idx_user_blocks_user",
                table: "user_blocks",
                column: "UserId");

            migrationBuilder.CreateIndex(
                name: "idx_friendships_user",
                table: "friendship_entity",
                column: "UserId");

            migrationBuilder.CreateIndex(
                name: "ix_conversations_participant1",
                table: "conversations",
                column: "Participant1Id");

            migrationBuilder.CreateIndex(
                name: "IX_UserLevels_CanClaimMedal",
                table: "UserLevels",
                column: "CanClaimMedal");

            migrationBuilder.CreateIndex(
                name: "IX_UserLevels_CurrentLevel",
                table: "UserLevels",
                column: "CurrentLevel");

            migrationBuilder.CreateIndex(
                name: "IX_UserDailyStats_Date",
                table: "UserDailyStats",
                column: "Date");

            migrationBuilder.CreateIndex(
                name: "IX_UserDailyStats_UserId",
                table: "UserDailyStats",
                column: "UserId");

            migrationBuilder.CreateIndex(
                name: "IX_UserDailyStats_UserId_Date",
                table: "UserDailyStats",
                columns: new[] { "UserId", "Date" },
                descending: new[] { false, true });

            migrationBuilder.CreateIndex(
                name: "IX_UltimaSubscriptions_Status",
                table: "UltimaSubscriptions",
                column: "Status");

            migrationBuilder.CreateIndex(
                name: "IX_UltimaSubscriptions_UserId",
                table: "UltimaSubscriptions",
                column: "UserId");

            migrationBuilder.CreateIndex(
                name: "IX_SpaceBoosts_UserId",
                table: "SpaceBoosts",
                column: "UserId");

            migrationBuilder.CreateIndex(
                name: "IX_SavedGifs_UserId",
                table: "SavedGifs",
                column: "UserId");

            migrationBuilder.CreateIndex(
                name: "IX_Reports_Category",
                table: "Reports",
                column: "Category");

            migrationBuilder.CreateIndex(
                name: "IX_Reports_CreatedAt",
                table: "Reports",
                column: "CreatedAt");

            migrationBuilder.CreateIndex(
                name: "IX_Reports_ReporterId",
                table: "Reports",
                column: "ReporterId");

            migrationBuilder.CreateIndex(
                name: "IX_Reports_Status",
                table: "Reports",
                column: "Status");

            migrationBuilder.CreateIndex(
                name: "IX_Reports_TargetId",
                table: "Reports",
                column: "TargetId");

            migrationBuilder.CreateIndex(
                name: "idx_reports_per_target",
                table: "Reports",
                columns: new[] { "ReporterId", "TargetId" });

            migrationBuilder.CreateIndex(
                name: "IX_ReportCases_LastReportedAt",
                table: "ReportCases",
                column: "LastReportedAt");

            migrationBuilder.CreateIndex(
                name: "IX_OperatorAuditLog_CreatedAt",
                table: "OperatorAuditLog",
                column: "CreatedAt");

            migrationBuilder.CreateIndex(
                name: "ix_notification_counters_updated_at",
                table: "NotificationCounters",
                column: "UpdatedAt");

            migrationBuilder.CreateIndex(
                name: "ix_notification_counters_user_id",
                table: "NotificationCounters",
                column: "UserId");

            migrationBuilder.CreateIndex(
                name: "IX_Items_Id_IsReference",
                table: "Items",
                columns: new[] { "Id", "IsReference" });

            migrationBuilder.CreateIndex(
                name: "IX_Items_Id_OwnerId",
                table: "Items",
                columns: new[] { "Id", "OwnerId" });

            migrationBuilder.CreateIndex(
                name: "IX_Items_IsReference",
                table: "Items",
                column: "IsReference");

            migrationBuilder.CreateIndex(
                name: "IX_Items_OwnerId",
                table: "Items",
                column: "OwnerId");

            migrationBuilder.CreateIndex(
                name: "IX_Items_TemplateId",
                table: "Items",
                column: "TemplateId");

            migrationBuilder.CreateIndex(
                name: "IX_FileBlobs_ExpiresAt",
                table: "FileBlobs",
                column: "ExpiresAt",
                filter: "\"IsDeleted\" = false")
                .Annotation("Npgsql:CreatedConcurrently", true);

            migrationBuilder.CreateIndex(
                name: "IX_FileBlobs_FileId",
                table: "FileBlobs",
                column: "FileId");

            migrationBuilder.CreateIndex(
                name: "IX_FeatureFlags_DefaultEnabled",
                table: "FeatureFlags",
                column: "DefaultEnabled");

            migrationBuilder.CreateIndex(
                name: "IX_FeatureFlagOverrides_Scope",
                table: "FeatureFlagOverrides",
                column: "Scope");

            migrationBuilder.CreateIndex(
                name: "IX_DeviceObservations_UserId",
                table: "DeviceObservations",
                column: "UserId");

            migrationBuilder.CreateIndex(
                name: "IX_ContentViolations_CreatedAt",
                table: "ContentViolations",
                column: "CreatedAt");

            migrationBuilder.CreateIndex(
                name: "ix_channel_read_states_user",
                table: "ChannelReadStates",
                column: "UserId");
        }
    }
}
