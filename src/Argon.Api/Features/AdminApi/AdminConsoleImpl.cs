namespace Argon.Api.Features.AdminApi;

using Argon.Features.Email;

using Argon.Api.Entities.Data;
using Argon.Api.Features.AdminApi.Diagnostics;
using Argon.Api.Grains.Interfaces;
using Argon.Core.Entities.Data;
using Argon.Core.Features.Logic;
using Argon.Features.Admin;
using Argon.Grains.Interfaces;
using ConsoleContracts;
using ion.runtime;
using Livekit.Server.Sdk.Dotnet;
using Argon.Services.Ion;
using Microsoft.Extensions.Caching.Hybrid;

public class AdminConsoleImpl(
    IGrainFactory grainFactory,
    ILogger<IAdminConsole> logger,
    IDbContextFactory<ApplicationDbContext> dbFactory,
    RuntimeDiagnosticsService runtimeDiagnostics,
    DatabaseDiagnosticsService databaseDiagnostics,
    KubernetesDiagnosticsService? kubernetesDiagnostics,
    NatsDiagnosticsService? natsDiagnostics,
    RedisDiagnosticsService? redisDiagnostics,
    OrleansDiagnosticsService? orleansDiagnostics,
    IOperatorCertificateService certificateService,
    IOperatorAuditService auditService,
    HybridCache lockdownCache,
    IUserSessionDiscoveryService sessionDiscovery,
    IUserSessionNotifier sessionNotifier,
    IOptions<AccountDeletionOptions> deletionOptions,
    IEmailJournal emailJournal
) : IAdminConsole
{
    public async Task<SearchUserResult> SearchUser(string query, CancellationToken ct = default)
    {
        if (string.IsNullOrWhiteSpace(query))
            return new SearchUserResult(false, null, SearchMatchKind.None);

        await using var db = await dbFactory.CreateDbContextAsync(ct);

        var normalizedQuery = query.Trim().ToLowerInvariant();

        // Try parse as GUID first
        if (Guid.TryParse(query, out var userId))
        {
            var exists = await db.Users.AnyAsync(u => u.Id == userId, ct);
            if (exists)
                return new SearchUserResult(true, userId, SearchMatchKind.UserId);
        }

        // Search by username
        var byUsername = await db.Users
           .Where(u => u.NormalizedUsername == normalizedQuery)
           .Select(u => u.Id)
           .FirstOrDefaultAsync(ct);
        if (byUsername != Guid.Empty)
            return new SearchUserResult(true, byUsername, SearchMatchKind.Username);

        // Search by email
        var byEmail = await db.Users
           .Where(u => u.NormalizedEmail == normalizedQuery)
           .Select(u => u.Id)
           .FirstOrDefaultAsync(ct);
        if (byEmail != Guid.Empty)
            return new SearchUserResult(true, byEmail, SearchMatchKind.Email);

        // Search by phone
        var byPhone = await db.Users
           .Where(u => u.PhoneNumber != null && u.PhoneNumber == query.Trim())
           .Select(u => u.Id)
           .FirstOrDefaultAsync(ct);
        if (byPhone != Guid.Empty)
            return new SearchUserResult(true, byPhone, SearchMatchKind.Phone);

        return new SearchUserResult(false, null, SearchMatchKind.None);
    }

    public async Task<UserCardDetails> GetUserCard(Guid userId, CancellationToken ct = default)
    {
        await using var db = await dbFactory.CreateDbContextAsync(ct);

        var user = await db.Users
                      .Include(u => u.Profile)
                      .Include(u => u.BotEntity)
                      .FirstOrDefaultAsync(u => u.Id == userId, ct)
                   ?? throw new InvalidOperationException("User not found");

        // Get last login from device history
        var lastLogin = await db.DeviceHistories
           .Where(d => d.UserId == userId && d.LastLoginTime != null)
           .OrderByDescending(d => d.LastLoginTime)
           .Select(d => d.LastLoginTime)
           .FirstOrDefaultAsync(ct);

        // Get blocked users count
        var blockedUsersCount = await db.UserBlocklist.CountAsync(b => b.UserId == userId, ct);

        // Get direct messages count (sent by user)
        var directMessagesCount = await db.DirectMessages.CountAsync(m => m.SenderId == userId, ct);

        // Get conversations count (where user is a participant)
        var conversationsCount = await db.Conversations
           .CountAsync(c => c.Participant1Id == userId || c.Participant2Id == userId, ct);

        // Account info
        var account = new UserAccountInfo(
            user.Id,
            user.Username,
            user.DisplayName,
            user.Email,
            user.PhoneNumber,
            user.AvatarFileId,
            user.DateOfBirth,
            user.CreatedAt.UtcDateTime,
            user.LockdownReason,
            user.LockDownExpiration?.UtcDateTime,
            user.LockDownIsAppealable,
            user.PreferredAuthMode,
            user.PreferredOtpMethod,
            user.AgreeTOS,
            lastLogin?.UtcDateTime,
            blockedUsersCount,
            directMessagesCount,
            conversationsCount
        );

        // Profile info
        var profile = user.Profile.ToDto();

        // Passkeys count & TwoFactor
        var passkeyCount = await db.Passkeys.CountAsync(p => p.UserId == userId && p.IsCompleted, ct);
        var hasTwoFactor = !string.IsNullOrEmpty(user.TotpSecret);

        // Items with box contents
        var itemEntities = await db.Items
           .Where(i => i.OwnerId == userId && !i.IsReference)
           .Include(i => i.Scenario)
           .ToListAsync(ct);

        var items = new List<InventoryItemInfo>();
        foreach (var item in itemEntities)
        {
            var isBox = item.Scenario is BoxScenario or QualifierBox or MultipleQualifierBox;
            var boxContents = IonArray<BoxContentInfo>.Empty;

            if (isBox && item.Scenario is QualifierBox qb && qb.ReferenceItemId != Guid.Empty)
            {
                var refItem = await db.Items.FirstOrDefaultAsync(i => i.Id == qb.ReferenceItemId, ct);
                if (refItem is not null)
                    boxContents = new IonArray<BoxContentInfo>([new BoxContentInfo(refItem.Id, refItem.TemplateId)]);
            }

            if (isBox && item.Scenario is MultipleQualifierBox { ReferenceItemIds.Count: > 0 } mqb)
            {
                var refItems = await db.Items.Where(i => mqb.ReferenceItemIds.Contains(i.Id)).ToListAsync(ct);
                if (refItems.Count > 0)
                    boxContents = new IonArray<BoxContentInfo>(refItems.Select(ri => new BoxContentInfo(ri.Id, ri.TemplateId)).ToList());
            }

            items.Add(new InventoryItemInfo(
                item.Id,
                item.TemplateId,
                item.IsUsable,
                item.IsGiftable,
                item.ReceivedFrom,
                item.TTL.HasValue ? (int)item.TTL.Value.TotalSeconds : null,
                item.CreatedAt.UtcDateTime,
                isBox,
                boxContents
            ));
        }

        // Recent messages (last 10)
        var messages = await db.Messages
           .Where(m => m.CreatorId == userId)
           .OrderByDescending(m => m.CreatedAt)
           .Take(10)
           .Select(m => new
           {
               m.MessageId,
               m.SpaceId,
               m.ChannelId,
               m.Text,
               m.CreatedAt
           })
           .ToListAsync(ct);

        var spaceIds = messages.Select(m => m.SpaceId).Distinct().ToList();
        var channelIds = messages.Select(m => m.ChannelId).Distinct().ToList();

        var spaceNames = await db.Spaces
           .Where(s => spaceIds.Contains(s.Id))
           .ToDictionaryAsync(s => s.Id, s => s.Name, ct);

        var channelNames = await db.Channels
           .Where(c => channelIds.Contains(c.Id))
           .ToDictionaryAsync(c => c.Id, c => c.Name, ct);

        var recentMessages = messages.Select(m => new MessageInfo(
            m.MessageId,
            m.SpaceId,
            spaceNames.GetValueOrDefault(m.SpaceId, "Unknown"),
            m.ChannelId,
            channelNames.GetValueOrDefault(m.ChannelId, "Unknown"),
            m.Text,
            m.CreatedAt.UtcDateTime
        )).ToArray();

        // Device history (last 10)
        var deviceHistoryEntities = await db.DeviceHistories
           .Where(d => d.UserId == userId)
           .OrderByDescending(d => d.LastLoginTime)
           .Take(10)
           .ToListAsync(ct);

        var deviceHistory = deviceHistoryEntities.Select(d => new DeviceHistoryInfo(
            d.MachineId,
            d.LastLoginTime?.UtcDateTime,
            d.LastKnownIP ?? "",
            d.RegionAddress ?? "",
            d.AppId ?? "",
            (ConsoleContracts.DeviceTypeKind)(int)d.DeviceType
        )).ToList();

        // Redeemed coupons
        var redemptionEntities = await db.CouponRedemption
           .Where(r => r.UserId == userId)
           .Include(r => r.Coupon)
           .Include(r => r.Items)
           .ToListAsync(ct);

        var redemptions = redemptionEntities.Select(r => new RedeemedCouponInfo(
            r.CouponId,
            r.Coupon.Code,
            r.RedeemedAt.UtcDateTime,
            r.Items.Count
        )).ToList();

        // Spaces (first 5)
        var spaceMemberships = await db.UsersToServerRelations
           .Where(sm => sm.UserId == userId && !sm.IsDeleted)
           .OrderByDescending(sm => sm.CreatedAt)
           .Take(5)
           .Include(sm => sm.Space)
           .ToListAsync(ct);

        var spaces = new List<UserSpaceInfo>();
        foreach (var sm in spaceMemberships)
        {
            var memberCount = await db.UsersToServerRelations.CountAsync(x => x.SpaceId == sm.SpaceId && !x.IsDeleted, ct);
            var channelCount = await db.Channels.CountAsync(c => c.SpaceId == sm.SpaceId, ct);
            var isOwner = sm.Space.CreatorId == userId;

            spaces.Add(new UserSpaceInfo(
                sm.SpaceId,
                sm.Space.Name,
                sm.Space.AvatarFileId,
                memberCount,
                channelCount,
                sm.CreatedAt.UtcDateTime,
                isOwner
            ));
        }

        // Level info
        var levelEntity = await db.UserLevels.FirstOrDefaultAsync(l => l.UserId == userId, ct);
        var level = levelEntity is not null
            ? new UserLevelInfo(
                levelEntity.CurrentLevel,
                levelEntity.CurrentCycleXp,
                levelEntity.TotalXpAllTime,
                levelEntity.CanClaimMedal,
                levelEntity.LastXpAward.UtcDateTime)
            : new UserLevelInfo(1, 0, 0, false, DateTime.UtcNow);

        // Stats (aggregated)
        var statsEntities = await db.UserDailyStats
           .Where(s => s.UserId == userId)
           .ToListAsync(ct);

        var stats = new UserStatsInfo(
            statsEntities.Sum(s => s.TimeInVoiceSeconds),
            statsEntities.Sum(s => s.CallsMade),
            statsEntities.Sum(s => s.MessagesSent),
            statsEntities.Sum(s => s.XpEarned)
        );

        // Teams (first 3)
        var teamMemberships = await db.MemberTeamEntities
           .Where(tm => tm.UserId == userId)
           .Take(3)
           .Include(tm => tm.Team)
           .Select(tm => new UserTeamInfo(
                tm.TeamId,
                tm.Team.Name,
                tm.Team.AvatarFileId,
                tm.IsOwner,
                tm.JoinedAt
            ))
           .ToListAsync(ct);

        // Friend count
        var friendCount = await db.Friends.CountAsync(f => f.UserId == userId, ct);

        // Auto-delete settings
        var autoDeleteEntity = await db.AutoDeleteSettings.FirstOrDefaultAsync(a => a.UserId == userId, ct);
        var autoDeleteSettings = autoDeleteEntity is not null
            ? new AutoDeleteSettingsInfo(autoDeleteEntity.Enabled, autoDeleteEntity.Months)
            : null;

        // Bots owned by user (through team membership)
        var userTeamIds = await db.MemberTeamEntities
           .Where(tm => tm.UserId == userId)
           .Select(tm => tm.TeamId)
           .ToListAsync(ct);

        var userBots = userTeamIds.Count > 0
            ? await db.BotEntities
               .Where(b => userTeamIds.Contains(b.TeamId))
               .Include(b => b.BotAsUser)
               .Include(b => b.Team)
               .Select(b => new AdminUserBotInfo(
                    b.AppId,
                    b.Name,
                    b.BotAsUser.Username,
                    b.IsVerified,
                    b.LifecycleState == BotLifecycleState.Published,
                    b.TeamId,
                    b.Team.Name
                ))
               .ToListAsync(ct)
            : [];

        // Premium info
        AdminPremiumInfo? premiumInfo = null;
        var subscription = await db.UltimaSubscriptions
           .Where(s => s.UserId == userId)
           .OrderByDescending(s => s.StartsAt)
           .FirstOrDefaultAsync(ct);

        if (subscription is not null)
        {
            var usedBoostSlots = await db.SpaceBoosts
               .CountAsync(b => b.SubscriptionId == subscription.Id && b.SpaceId != null, ct);
            premiumInfo = new AdminPremiumInfo(
                subscription.Id,
                (UltimaPlan)(int)subscription.Tier,
                (UltimaSubscriptionStatus)(int)subscription.Status,
                subscription.StartsAt.UtcDateTime,
                subscription.ExpiresAt.UtcDateTime,
                subscription.AutoRenew,
                subscription.BoostSlots,
                usedBoostSlots,
                subscription.CancelledAt?.UtcDateTime,
                subscription.XsollaSubscriptionId,
                subscription.ActivatedFromItemId
            );
        }

        // Bot flag & user flags
        var isBot = user.BotEntityId is not null;
        var flags = (UserFlag)(int)UserEntity.GetFlags(user);

        return new UserCardDetails(
            account,
            profile,
            passkeyCount,
            hasTwoFactor,
            new IonArray<InventoryItemInfo>(items),
            new IonArray<MessageInfo>(recentMessages),
            new IonArray<DeviceHistoryInfo>(deviceHistory),
            new IonArray<RedeemedCouponInfo>(redemptions),
            new IonArray<UserSpaceInfo>(spaces),
            level,
            stats,
            new IonArray<UserTeamInfo>(teamMemberships),
            friendCount,
            autoDeleteSettings,
            new IonArray<AdminUserBotInfo>(userBots),
            premiumInfo,
            isBot,
            flags
        );
    }

    public async Task<PlatformStats> GetPlatformStats(CancellationToken ct = default)
    {
        await using var db = await dbFactory.CreateDbContextAsync(ct);

        var totalUsers = await db.Users.LongCountAsync(ct);
        var totalSpaces = await db.Spaces.LongCountAsync(ct);
        var totalChannels = await db.Channels.LongCountAsync(ct);
        var totalMessages = await db.Messages.LongCountAsync(ct);
        var totalCustomItems = await db.Items.Where(i => i.IsReference).LongCountAsync(ct);
        var totalCouponsRedeemed = await db.CouponRedemption.LongCountAsync(ct);
        var totalApps = await db.AppEntities.LongCountAsync(ct);
        var totalBots = await db.BotEntities.LongCountAsync(ct);

        var oneMonthAgo = DateTimeOffset.UtcNow.AddMonths(-1);

        // New users in last month
        var newUsersLast1Month = await db.Users
           .Where(u => u.CreatedAt >= oneMonthAgo)
           .LongCountAsync(ct);

        // Active users in last month (users who logged in)
        var activeUsersLast1Month = await db.DeviceHistories
           .Where(d => d.LastLoginTime >= oneMonthAgo)
           .Select(d => d.UserId)
           .Distinct()
           .LongCountAsync(ct);

        // Peak active users in last month (max unique users per day)
        var dailyActiveUsers = await db.DeviceHistories
           .Where(d => d.LastLoginTime >= oneMonthAgo && d.LastLoginTime != null)
           .GroupBy(d => d.LastLoginTime!.Value.Date)
           .Select(g => g.Select(x => x.UserId).Distinct().Count())
           .ToListAsync(ct);
        var peakActiveUsersLast1Month = dailyActiveUsers.Count > 0 ? dailyActiveUsers.Max() : 0;

        // New spaces in last month
        var newSpacesLast1Month = await db.Spaces
           .Where(s => s.CreatedAt >= oneMonthAgo)
           .LongCountAsync(ct);

        // Active spaces in last month (spaces with messages)
        var activeSpacesLast1Month = await db.Messages
           .Where(m => m.CreatedAt >= oneMonthAgo)
           .Select(m => m.SpaceId)
           .Distinct()
           .LongCountAsync(ct);

        // Peak active spaces in last month (max unique spaces with messages per day)
        var dailyActiveSpaces = await db.Messages
           .Where(m => m.CreatedAt >= oneMonthAgo)
           .GroupBy(m => m.CreatedAt.Date)
           .Select(g => g.Select(x => x.SpaceId).Distinct().Count())
           .ToListAsync(ct);
        var peakActiveSpacesLast1Month = dailyActiveSpaces.Count > 0 ? dailyActiveSpaces.Max() : 0;

        return new PlatformStats(
            totalUsers,
            totalSpaces,
            totalChannels,
            totalMessages,
            totalCustomItems,
            totalCouponsRedeemed,
            newUsersLast1Month,
            activeUsersLast1Month,
            peakActiveUsersLast1Month,
            newSpacesLast1Month,
            activeSpacesLast1Month,
            peakActiveSpacesLast1Month,
            totalApps,
            totalBots
        );
    }

    public async Task<ItemTemplateList> GetItemTemplates(CancellationToken ct = default)
    {
        await using var db = await dbFactory.CreateDbContextAsync(ct);

        var templates = await db.Items
           .Where(i => i.IsReference)
           .Include(i => i.Scenario)
           .ToListAsync(ct);

        var result = new List<ItemTemplateInfo>();
        foreach (var t in templates)
        {
            var scenarioType = t.Scenario switch
            {
                RedeemScenario => ItemScenarioKind.RedeemCode,
                PremiumScenario => ItemScenarioKind.Premium,
                QualifierBox => ItemScenarioKind.QualifierBox,
                MultipleQualifierBox => ItemScenarioKind.QualifierBox,
                BoxScenario => ItemScenarioKind.Box,
                _ => ItemScenarioKind.None
            };

            var boxContents = IonArray<BoxContentInfo>.Empty;
            switch (t.Scenario)
            {
                case QualifierBox qb when qb.ReferenceItemId != Guid.Empty:
                {
                    var refItem = await db.Items.FirstOrDefaultAsync(i => i.Id == qb.ReferenceItemId, ct);
                    if (refItem is not null)
                        boxContents = new IonArray<BoxContentInfo>([new BoxContentInfo(refItem.Id, refItem.TemplateId)]);
                    break;
                }
                case MultipleQualifierBox { ReferenceItemIds.Count: > 0 } mqb:
                {
                    var refItems = await db.Items.Where(i => mqb.ReferenceItemIds.Contains(i.Id)).ToListAsync(ct);
                    if (refItems.Count > 0)
                        boxContents = new IonArray<BoxContentInfo>(refItems.Select(ri => new BoxContentInfo(ri.Id, ri.TemplateId)).ToList());
                    break;
                }
            }

            result.Add(new ItemTemplateInfo(
                t.Id,
                t.TemplateId,
                t.IsUsable,
                t.IsGiftable,
                t.IsAffectBadge,
                t.TTL.HasValue ? (int)t.TTL.Value.TotalSeconds : null,
                t.CreatedAt.UtcDateTime,
                scenarioType,
                boxContents
            ));
        }

        return new ItemTemplateList(new IonArray<ItemTemplateInfo>(result));
    }

    public async Task<DeleteItemResult> DeleteItemFromUserInventory(Guid userId, Guid itemId, CancellationToken ct = default)
    {
        try
        {
            await using var db = await dbFactory.CreateDbContextAsync(ct);

            var userExists = await db.Users.AnyAsync(u => u.Id == userId, ct);
            if (!userExists)
            {
                logger.LogWarning("DeleteItemFromUserInventory failed: user {UserId} not found", userId);
                return new DeleteItemResult(false, null, $"User {userId} not found");
            }

            var item = await db.Items
               .Include(i => i.Scenario)
               .FirstOrDefaultAsync(i => i.Id == itemId, ct);

            if (item is null)
            {
                logger.LogWarning("DeleteItemFromUserInventory failed: item {ItemId} not found", itemId);
                return new DeleteItemResult(false, null, $"Item {itemId} not found");
            }

            if (item.IsReference)
            {
                logger.LogWarning("DeleteItemFromUserInventory failed: cannot delete reference template {ItemId} from user inventory", itemId);
                return new DeleteItemResult(false, null, $"Cannot delete reference template {itemId}, use DeleteItemTemplate instead");
            }

            if (item.OwnerId != userId)
            {
                logger.LogWarning("DeleteItemFromUserInventory failed: item {ItemId} does not belong to user {UserId} (actual owner: {OwnerId})",
                    itemId, userId, item.OwnerId);
                return new DeleteItemResult(false, null, $"Item {itemId} does not belong to user {userId}");
            }

            db.Items.Remove(item);
            var rowsAffected = await db.SaveChangesAsync(ct);

            if (rowsAffected == 0)
            {
                logger.LogError("DeleteItemFromUserInventory failed: no rows affected when deleting item {ItemId} from user {UserId}", itemId,
                    userId);
                return new DeleteItemResult(false, null, $"Failed to delete item {itemId} from database");
            }

            logger.LogInformation("Deleted item {ItemId} (template: '{TemplateId}') from user {UserId} inventory",
                itemId, item.TemplateId, userId);
            return new DeleteItemResult(true, itemId, null);
        }
        catch (DbUpdateException ex)
        {
            logger.LogError(ex, "Database error while deleting item {ItemId} from user {UserId} inventory", itemId, userId);
            return new DeleteItemResult(false, null, $"Database error while deleting item {itemId}");
        }
        catch (Exception ex)
        {
            logger.LogError(ex, "Unexpected error while deleting item {ItemId} from user {UserId} inventory", itemId, userId);
            return new DeleteItemResult(false, null, $"Unexpected error while deleting item {itemId}");
        }
    }

    public async Task<DeleteItemResult> DeleteItemTemplate(Guid itemId, CancellationToken ct = default)
    {
        try
        {
            await using var db = await dbFactory.CreateDbContextAsync(ct);


            var item = await db.Items
               .Include(i => i.Scenario)
               .FirstOrDefaultAsync(i => i.Id == itemId, ct);

            if (item is null)
            {
                logger.LogWarning("DeleteItemTemplate failed: item {ItemId} not found", itemId);
                return new DeleteItemResult(false, null, $"DeleteItemTemplate failed: item {itemId} not found");
            }

            if (!item.IsReference)
            {
                logger.LogWarning("DeleteItemTemplate failed: item {ItemId} is not a reference template", itemId);
                return new DeleteItemResult(false, null, $"DeleteItemTemplate failed: item {itemId} is not a reference template");
            }

            var usedInCoupons = await db.Coupons.AnyAsync(c => c.ReferenceItemEntityId == itemId, ct);
            if (usedInCoupons)
            {
                logger.LogWarning("DeleteItemTemplate failed: template {ItemId} is used in coupons", itemId);
                return new DeleteItemResult(false, null, $"DeleteItemTemplate failed: template {itemId} is used in coupons");
            }

            var usedInQualifierBoxes = await db.Items
               .Where(i => i.IsReference && i.Scenario != null)
               .Include(argonItemEntity => argonItemEntity.Scenario)
               .ToListAsync(ct);

            var isUsedInBox = usedInQualifierBoxes.Any(i =>
                (i.Scenario is QualifierBox qb && qb.ReferenceItemId == itemId) ||
                (i.Scenario is MultipleQualifierBox mqb && mqb.ReferenceItemIds.Contains(itemId)));

            if (isUsedInBox)
            {
                logger.LogWarning("DeleteItemTemplate failed: template {ItemId} is used in other box templates", itemId);
                return new DeleteItemResult(false, null, $"DeleteItemTemplate failed: template {itemId} is used in other box templates");
            }

            db.Items.Remove(item);
            var rowsAffected = await db.SaveChangesAsync(ct);

            if (rowsAffected == 0)
            {
                logger.LogError("DeleteItemTemplate failed: no rows affected when deleting template {ItemId}", itemId);
                return new DeleteItemResult(false, null, $"DeleteItemTemplate failed: no rows affected when deleting template {itemId}");
            }

            logger.LogInformation("Deleted item template {ItemId} with TemplateId '{TemplateId}'", itemId, item.TemplateId);
            return new DeleteItemResult(true, itemId, null);
        }
        catch (DbUpdateException ex)
        {
            logger.LogError(ex, "Database error while deleting item template {ItemId}", itemId);
            return new DeleteItemResult(false, null, $"Database error while deleting item template {itemId}");
        }
        catch (Exception ex)
        {
            logger.LogError(ex, "Unexpected error while deleting item template {ItemId}", itemId);
            return new DeleteItemResult(false, null, $"Unexpected error while deleting item template {itemId}");
        }
    }

    public async Task<CreateItemTemplateResult> CreateItemTemplate(CreateItemTemplateInput input, CancellationToken ct = default)
    {
        try
        {
            await using var db = await dbFactory.CreateDbContextAsync(ct);

            if (string.IsNullOrWhiteSpace(input.templateId))
            {
                logger.LogWarning("CreateItemTemplate failed: templateId is empty");
                return new CreateItemTemplateResult(false, null, "Template ID cannot be empty");
            }

            List<Guid>? boxContentIds = null;
            if (input is { scenarioType: ItemScenarioKind.QualifierBox, boxContentTemplateIds.Size: > 0 })
            {
                try
                {
                    boxContentIds = input.boxContentTemplateIds.Values.Select(Guid.Parse).ToList();
                }
                catch (FormatException)
                {
                    logger.LogWarning("CreateItemTemplate failed: invalid GUID format in boxContentTemplateIds");
                    return new CreateItemTemplateResult(false, null, "One or more box content IDs have invalid format");
                }

                var existingItems = await db.Items
                   .Where(i => i.IsReference && boxContentIds.Contains(i.Id))
                   .Select(i => i.Id)
                   .ToListAsync(ct);

                var missingIds = boxContentIds.Except(existingItems).ToList();
                if (missingIds.Count > 0)
                {
                    logger.LogWarning("CreateItemTemplate failed: reference items not found: {MissingIds}", string.Join(", ", missingIds));
                    return new CreateItemTemplateResult(false, null, $"Reference items not found: {string.Join(", ", missingIds)}");
                }

                var existingBoxTemplates = await db.Items
                   .Where(i => i.IsReference && i.Scenario != null)
                   .Include(i => i.Scenario)
                   .ToListAsync(ct);

                foreach (var existingTemplate in existingBoxTemplates)
                {
                    var isDuplicate = false;

                    switch (boxContentIds.Count)
                    {
                        case 1 when existingTemplate.Scenario is QualifierBox qb:
                        isDuplicate = qb.ReferenceItemId == boxContentIds[0];
                        break;
                        case > 1 when existingTemplate.Scenario is MultipleQualifierBox mqb:
                        {
                            var existingSet = mqb.ReferenceItemIds.OrderBy(x => x).ToList();
                            var newSet = boxContentIds.OrderBy(x => x).ToList();
                            isDuplicate = existingSet.SequenceEqual(newSet);
                            break;
                        }
                    }

                    if (isDuplicate)
                    {
                        logger.LogWarning("CreateItemTemplate failed: box template with same content already exists (ID: {ExistingId}, TemplateId: '{ExistingTemplateId}')",
                            existingTemplate.Id, existingTemplate.TemplateId);
                        return new CreateItemTemplateResult(false, null,
                            $"Box template with same content already exists (ID: {existingTemplate.Id}, TemplateId: '{existingTemplate.TemplateId}')");
                    }
                }
            }
            else if (input.scenarioType is not ItemScenarioKind.Box and not ItemScenarioKind.QualifierBox)
            {
                var existingTemplate = await db.Items.AnyAsync(i => i.IsReference && i.TemplateId == input.templateId, ct);
                if (existingTemplate)
                {
                    logger.LogWarning("CreateItemTemplate failed: template with ID '{TemplateId}' already exists", input.templateId);
                    return new CreateItemTemplateResult(false, null, $"Template with ID '{input.templateId}' already exists");
                }
            }

            ItemUseScenario? scenario = input.scenarioType switch
            {
                ItemScenarioKind.Box => new BoxScenario
                {
                    Key = ArgonId.New(),
                    Edition = input.templateId
                },
                ItemScenarioKind.QualifierBox when boxContentIds?.Count == 1 => new QualifierBox
                {
                    Key = ArgonId.New(),
                    ReferenceItemId = boxContentIds[0]
                },
                ItemScenarioKind.QualifierBox when boxContentIds?.Count > 1 => new MultipleQualifierBox
                {
                    Key = ArgonId.New(),
                    ReferenceItemIds = boxContentIds
                },
                ItemScenarioKind.Premium => new PremiumScenario
                {
                    Key = ArgonId.New(),
                    PlanId = ""
                },
                ItemScenarioKind.RedeemCode => new RedeemScenario
                {
                    Key = ArgonId.New(),
                    Code = "",
                    ServiceKey = ""
                },
                _ => null
            };

            var item = new ArgonItemEntity
            {
                Id = ArgonId.New(),
                TemplateId = input.templateId,
                IsUsable = input.isUsable,
                IsGiftable = input.isGiftable,
                IsAffectBadge = input.isAffectBadge,
                IsReference = true,
                OwnerId = UserEntity.SystemUser,
                TTL = input.ttl.HasValue ? TimeSpan.FromSeconds(input.ttl.Value) : null,
                Scenario = scenario,
                ScenarioKey = scenario?.Key
            };

            db.Items.Add(item);
            var rowsAffected = await db.SaveChangesAsync(ct);

            if (rowsAffected == 0)
            {
                logger.LogError("CreateItemTemplate failed: no rows affected when saving template '{TemplateId}'", input.templateId);
                return new CreateItemTemplateResult(false, null, "Failed to save item template to database");
            }

            logger.LogInformation("Created item template '{TemplateId}' with ID {ItemId}, type {ScenarioType}",
                input.templateId, item.Id, input.scenarioType);

            return new CreateItemTemplateResult(true, item.Id, null);
        }
        catch (DbUpdateException ex)
        {
            logger.LogError(ex, "Database error while creating item template '{TemplateId}'", input.templateId);
            return new CreateItemTemplateResult(false, null, "Database error occurred while creating template");
        }
        catch (Exception ex)
        {
            logger.LogError(ex, "Unexpected error while creating item template '{TemplateId}'", input.templateId);
            return new CreateItemTemplateResult(false, null, $"Unexpected error: {ex.Message}");
        }
    }

    public async Task<CouponList> GetCoupons(CancellationToken ct = default)
    {
        await using var db = await dbFactory.CreateDbContextAsync(ct);

        var couponEntities = await db.Coupons
           .Include(c => c.ReferenceItemEntity)
           .ToListAsync(ct);

        var coupons = couponEntities.Select(c => new CouponInfo(
            c.Id,
            c.Code,
            c.Description,
            c.ValidFrom.UtcDateTime,
            c.ValidTo.UtcDateTime,
            c.MaxRedemptions,
            c.RedemptionCount,
            c.IsActive,
            c.ReferenceItemEntity?.TemplateId
        )).ToList();

        return new CouponList(new IonArray<CouponInfo>(coupons));
    }

    public async Task<CreateCouponResult> CreateCoupon(CreateCouponInput input, CancellationToken ct = default)
    {
        try
        {
            await using var db = await dbFactory.CreateDbContextAsync(ct);

            var exists = await db.Coupons.AnyAsync(c => c.Code == input.code, ct);
            if (exists)
                return new CreateCouponResult(false, null, "Coupon with this code already exists");

            var coupon = new ArgonCouponEntity
            {
                Id = ArgonId.New(),
                Code = input.code,
                Description = input.description,
                ValidFrom = input.validFrom,
                ValidTo = input.validTo,
                MaxRedemptions = input.maxRedemptions,
                RedemptionCount = 0,
                IsActive = true,
                ReferenceItemEntityId = input.referenceItemId
            };

            db.Coupons.Add(coupon);
            await db.SaveChangesAsync(ct);

            return new CreateCouponResult(true, coupon.Id, null);
        }
        catch (Exception ex)
        {
            return new CreateCouponResult(false, null, ex.Message);
        }
    }

    public async Task<UserActionResult> BlockUser(Guid userId, LockdownReason reason, DateTimeOffset? expiration, bool isAppealable,
        CancellationToken ct = default)
    {
        try
        {
            await using var db = await dbFactory.CreateDbContextAsync(ct);

            var user = await db.Users.FirstOrDefaultAsync(u => u.Id == userId, ct);
            if (user is null)
                return new UserActionResult(false, "User not found");

            user.LockdownReason = reason;
            user.LockDownExpiration = expiration;
            user.LockDownIsAppealable = isAppealable;

            await db.SaveChangesAsync(ct);
            await lockdownCache.RemoveAsync(ArgonRequestContext.LockdownCacheKey(userId), ct);
            await auditService.LogAsync("BlockUser", "User", userId.ToString(),
                $"Reason={reason}, expiration={expiration}, appealable={isAppealable}");
            return new UserActionResult(true, null);
        }
        catch (Exception ex)
        {
            return new UserActionResult(false, ex.Message);
        }
    }

    public async Task<UserActionResult> UnblockUser(Guid userId, CancellationToken ct = default)
    {
        try
        {
            await using var db = await dbFactory.CreateDbContextAsync(ct);

            var user = await db.Users.FirstOrDefaultAsync(u => u.Id == userId, ct);
            if (user is null)
                return new UserActionResult(false, "User not found");

            user.LockdownReason = LockdownReason.NONE;
            user.LockDownExpiration = null;
            user.LockDownIsAppealable = false;

            await db.SaveChangesAsync(ct);
            await lockdownCache.RemoveAsync(ArgonRequestContext.LockdownCacheKey(userId), ct);
            await auditService.LogAsync("UnblockUser", "User", userId.ToString());
            return new UserActionResult(true, null);
        }
        catch (Exception ex)
        {
            return new UserActionResult(false, ex.Message);
        }
    }

    public async Task<DeviceList> GetUserDevices(Guid userId, CancellationToken ct = default)
    {
        await using var db = await dbFactory.CreateDbContextAsync(ct);

        var now = DateTimeOffset.UtcNow;

        var devices = await db.DeviceObservations
           .Where(o => o.UserId == userId)
           .Join(db.DeviceKeys, o => o.DeviceId, k => k.DeviceId, (o, k) => k)
           .OrderByDescending(k => k.LastProvenAt)
           .ToListAsync(ct);

        var summaries = new List<DeviceSummary>(devices.Count);

        foreach (var key in devices)
        {
            summaries.Add(new DeviceSummary(
                key.DeviceId,
                (int)key.Platform,
                (int)key.Assurance,
                key.ClientName,
                key.EnrolledAt,
                key.LastProvenAt,
                await db.DeviceObservations.CountAsync(o => o.DeviceId == key.DeviceId, ct),
                await db.DeviceBans.AnyAsync(b => b.DeviceId == key.DeviceId && (b.ExpiresAt == null || b.ExpiresAt > now), ct)));
        }

        return new DeviceList(summaries);
    }

    /// <summary>
    /// Everyone who has signed in from one machine.
    /// </summary>
    /// <remarks>
    /// The evidence a device ban is decided on, and the collateral it will cause. A shared family
    /// computer and an alt farm produce the same list, so this exists to be read by a person before
    /// <see cref="BanDevice"/> is used rather than to be counted by anything automatic.
    /// </remarks>
    public async Task<DeviceAccountList> GetDeviceAccounts(Guid deviceId, CancellationToken ct = default)
    {
        await using var db = await dbFactory.CreateDbContextAsync(ct);

        var accounts = await db.DeviceObservations
           .Where(o => o.DeviceId == deviceId)
           .Join(db.Users, o => o.UserId, u => u.Id, (o, u) => new { o, u })
           .OrderByDescending(x => x.o.LastSeenAt)
           .Select(x => new DeviceAccount(
                x.u.Id,
                x.u.Username,
                x.u.DisplayName,
                x.o.FirstSeenAt,
                x.o.LastSeenAt,
                x.o.Logins,
                x.u.LockdownReason != LockdownReason.NONE))
           .ToListAsync(ct);

        return new DeviceAccountList(accounts);
    }

    public async Task<UserActionResult> BanDevice(Guid deviceId, string reason, DateTimeOffset? expiration, CancellationToken ct = default)
    {
        try
        {
            await using var db = await dbFactory.CreateDbContextAsync(ct);

            // Only an enrolled machine can be barred: a device the server cannot recognise on sight
            // would shed the ban by clearing a cookie, and a ban that does nothing is worse than
            // none, because it looks like something was done.
            if (!await db.DeviceKeys.AnyAsync(k => k.DeviceId == deviceId, ct))
                return new UserActionResult(false, "Device not found");

            var existing = await db.DeviceBans
               .IgnoreQueryFilters()
               .FirstOrDefaultAsync(b => b.DeviceId == deviceId, ct);

            if (existing is null)
                db.DeviceBans.Add(new DeviceBanEntity
                {
                    Id        = ArgonId.New(),
                    DeviceId  = deviceId,
                    Reason    = reason,
                    ExpiresAt = expiration
                });
            else
            {
                // Revived rather than inserted alongside: soft delete leaves the old row holding the
                // unique index, so a second insert would collide with something nobody can see.
                existing.IsDeleted = false;
                existing.DeletedAt = null;
                existing.Reason    = reason;
                existing.ExpiresAt = expiration;
            }

            await db.SaveChangesAsync(ct);
            await auditService.LogAsync("BanDevice", "Device", deviceId.ToString(), $"Reason={reason}, expiration={expiration}");

            return new UserActionResult(true, null);
        }
        catch (Exception ex)
        {
            return new UserActionResult(false, ex.Message);
        }
    }

    public async Task<UserActionResult> UnbanDevice(Guid deviceId, CancellationToken ct = default)
    {
        try
        {
            await using var db = await dbFactory.CreateDbContextAsync(ct);

            var ban = await db.DeviceBans.FirstOrDefaultAsync(b => b.DeviceId == deviceId, ct);

            if (ban is null)
                return new UserActionResult(false, "Device is not banned");

            db.DeviceBans.Remove(ban);

            await db.SaveChangesAsync(ct);
            await auditService.LogAsync("UnbanDevice", "Device", deviceId.ToString());

            return new UserActionResult(true, null);
        }
        catch (Exception ex)
        {
            return new UserActionResult(false, ex.Message);
        }
    }

    public async Task<UserActionResult> GrantXp(Guid userId, int amount, CancellationToken ct = default)
    {
        try
        {
            await using var db = await dbFactory.CreateDbContextAsync(ct);

            var userExists = await db.Users.AnyAsync(u => u.Id == userId, ct);
            if (!userExists)
                return new UserActionResult(false, "User not found");

            var grain = grainFactory.GetGrain<IUserLevelGrain>(userId);
            await grain.AwardXpAsync(amount, XpSource.Event);

            await auditService.LogAsync("GrantXp", "User", userId.ToString(), $"Amount={amount}");
            return new UserActionResult(true, null);
        }
        catch (Exception ex)
        {
            return new UserActionResult(false, ex.Message);
        }
    }

    public async Task<UserActionResult> GrantItem(Guid userId, Guid itemId, CancellationToken ct = default)
    {
        try
        {
            await using var db = await dbFactory.CreateDbContextAsync(ct);

            var refItem = await db.Items.FirstOrDefaultAsync(i => i.IsReference && i.Id == itemId, ct);
            if (refItem is null)
                return new UserActionResult(false, "Reference item not found");

            var inventoryGrain = grainFactory.GetGrain<IInventoryGrain>(Guid.Empty);
            var success = await inventoryGrain.GiveItemFor(userId, itemId, ct);

            if (!success)
                return new UserActionResult(false, "Failed to grant item");

            await auditService.LogAsync("GrantItem", "User", userId.ToString(), $"ItemId={itemId}");
            return new UserActionResult(true, null);
        }
        catch (Exception ex)
        {
            return new UserActionResult(false, ex.Message);
        }
    }

    public async Task<UserActionResult> ChangeUsername(Guid userId, string newUsername, CancellationToken ct = default)
    {
        try
        {
            await using var db = await dbFactory.CreateDbContextAsync(ct);

            var user = await db.Users.FirstOrDefaultAsync(u => u.Id == userId, ct);
            if (user is null)
                return new UserActionResult(false, "User not found");

            var normalizedNew = newUsername.ToLowerInvariant();
            var taken = await db.Users.AnyAsync(u => u.NormalizedUsername == normalizedNew && u.Id != userId, ct);
            if (taken)
                return new UserActionResult(false, "Username already taken");

            user.Username = newUsername;
            await db.SaveChangesAsync(ct);

            await auditService.LogAsync("ChangeUsername", "User", userId.ToString(), $"NewUsername={newUsername}");
            return new UserActionResult(true, null);
        }
        catch (Exception ex)
        {
            return new UserActionResult(false, ex.Message);
        }
    }

    public async Task<UserActionResult> RemoveTwoFactor(Guid userId, CancellationToken ct = default)
    {
        try
        {
            await using var db = await dbFactory.CreateDbContextAsync(ct);


            var user = await db.Users.FirstOrDefaultAsync(u => u.Id == userId, ct);
            if (user is null)
                return new UserActionResult(false, "User not found");

            user.TotpSecret = null;
            await db.SaveChangesAsync(ct);

            await auditService.LogAsync("RemoveTwoFactor", "User", userId.ToString());
            return new UserActionResult(true, null);
        }
        catch (Exception ex)
        {
            return new UserActionResult(false, ex.Message);
        }
    }

    public async Task<UserActionResult> RemovePhoneNumber(Guid userId, CancellationToken ct = default)
    {
        try
        {
            await using var db = await dbFactory.CreateDbContextAsync(ct);


            var user = await db.Users.FirstOrDefaultAsync(u => u.Id == userId, ct);
            if (user is null)
                return new UserActionResult(false, "User not found");

            user.PhoneNumber = null;
            await db.SaveChangesAsync(ct);

            await auditService.LogAsync("RemovePhoneNumber", "User", userId.ToString());
            return new UserActionResult(true, null);
        }
        catch (Exception ex)
        {
            return new UserActionResult(false, ex.Message);
        }
    }

    public async Task<UserActionResult> ChangeEmail(Guid userId, string newEmail, CancellationToken ct = default)
    {
        try
        {
            await using var db = await dbFactory.CreateDbContextAsync(ct);

            var user = await db.Users.FirstOrDefaultAsync(u => u.Id == userId, ct);
            if (user is null)
                return new UserActionResult(false, "User not found");

            var normalizedNew = newEmail.ToLowerInvariant();
            var taken = await db.Users.AnyAsync(u => u.NormalizedEmail == normalizedNew && u.Id != userId, ct);
            if (taken)
                return new UserActionResult(false, "Email already taken");

            user.Email = newEmail;
            await db.SaveChangesAsync(ct);

            await auditService.LogAsync("ChangeEmail", "User", userId.ToString(), $"NewEmail={newEmail}");
            return new UserActionResult(true, null);
        }
        catch (Exception ex)
        {
            return new UserActionResult(false, ex.Message);
        }
    }

    public async Task<DiagnosticsResult> GetDiagnostics(CancellationToken ct = default)
    {
        var runtimeTask  = runtimeDiagnostics.GetDiagnosticsAsync(ct);
        var databaseTask = databaseDiagnostics?.GetDiagnosticsAsync(ct) ?? Task.FromResult<DatabaseDiagnostics?>(null);
        var k8sTask      = kubernetesDiagnostics?.GetDiagnosticsAsync(ct) ?? Task.FromResult<KubernetesDiagnostics?>(null);
        var natsTask     = natsDiagnostics?.GetDiagnosticsAsync(ct) ?? Task.FromResult<NatsDiagnostics?>(null);
        var redisTask    = redisDiagnostics?.GetDiagnosticsAsync(ct) ?? Task.FromResult<RedisDiagnostics?>(null);
        var orleansTask  = orleansDiagnostics?.GetDiagnosticsAsync(ct) ?? Task.FromResult<OrleansDiagnostics?>(null);

        await Task.WhenAll(runtimeTask, databaseTask, k8sTask, natsTask, redisTask, orleansTask);

        return new DiagnosticsResult(
            await runtimeTask,
            await databaseTask,
            await k8sTask,
            await natsTask,
            await redisTask,
            await orleansTask,
            DateTime.UtcNow
        );
    }

    public async Task<OperatorList> GetOperators(CancellationToken ct = default)
    {
        await using var db = await dbFactory.CreateDbContextAsync(ct);

        var entities = await db.Operators
           .Include(o => o.Certificates.Where(c => !c.IsDeleted))
           .Where(o => !o.IsDeleted)
           .OrderByDescending(o => o.CreatedAt)
           .ToListAsync(ct);

        var operators = entities.Select(MapOperatorInfo).ToList();

        return new OperatorList(new IonArray<OperatorInfo>(operators));
    }

    public async Task<OperatorDetails> GetOperatorDetails(Guid operatorId, CancellationToken ct = default)
    {
        await using var db = await dbFactory.CreateDbContextAsync(ct);

        var op = await db.Operators
                    .Include(o => o.Certificates.Where(c => !c.IsDeleted))
                    .Include(o => o.User)
                    .ThenInclude(u => u!.Profile)
                    .FirstOrDefaultAsync(o => o.Id == operatorId && !o.IsDeleted, ct)
                 ?? throw new InvalidOperationException("Operator not found");

        var info = MapOperatorInfo(op);

        UserAccountInfo? account = null;
        ArgonUserProfile? profile = null;
        if (op.User is { } user)
        {
            account = new UserAccountInfo(
                user.Id,
                user.Username,
                user.DisplayName,
                user.Email,
                user.PhoneNumber,
                user.AvatarFileId,
                user.DateOfBirth,
                user.CreatedAt.UtcDateTime,
                user.LockdownReason,
                user.LockDownExpiration?.UtcDateTime,
                user.LockDownIsAppealable,
                user.PreferredAuthMode,
                user.PreferredOtpMethod,
                user.AgreeTOS,
                null,
                0, 0, 0
            );
            profile = user.Profile?.ToDto();
        }

        var recentAudit = await auditService.GetRecentByOperatorAsync(operatorId, 20, ct);
        var auditEntries = recentAudit.Select(MapAuditEntry).ToList();

        return new OperatorDetails(info, account, profile, new IonArray<AuditEntry>(auditEntries));
    }

    public async Task<CreateOperatorResult> CreateOperator(CreateOperatorInput input, CancellationToken ct = default)
    {
        try
        {
            await using var db = await dbFactory.CreateDbContextAsync(ct);

            var caller = OperatorRequestContext.Current;
            if (!await db.Operators.AnyAsync(o => o.Id == caller.OperatorId && !o.IsDeleted && o.IsSystemOperator, ct))
                return new CreateOperatorResult(false, null, "Only system operators can create new operators");

            if (string.IsNullOrWhiteSpace(input.displayName) || string.IsNullOrWhiteSpace(input.email))
                return new CreateOperatorResult(false, null, "Display name and email are required");

            var exists = await db.Operators.AnyAsync(o => o.Email == input.email && !o.IsDeleted, ct);
            if (exists)
                return new CreateOperatorResult(false, null, "An operator with this email already exists");

            if (input.userId.HasValue)
            {
                var userExists = await db.Users.AnyAsync(u => u.Id == input.userId.Value, ct);
                if (!userExists)
                    return new CreateOperatorResult(false, null, "Linked user not found");

                var alreadyLinked = await db.Operators.AnyAsync(o => o.UserId == input.userId.Value && !o.IsDeleted, ct);
                if (alreadyLinked)
                    return new CreateOperatorResult(false, null, "This user is already linked to another operator");
            }

            var entity = new OperatorEntity
            {
                DisplayName      = input.displayName.Trim(),
                Email            = input.email.Trim().ToLowerInvariant(),
                UserId           = input.userId,
                IsActive         = true,
                IsSystemOperator = input.isSystemOperator
            };

            db.Operators.Add(entity);
            await db.SaveChangesAsync(ct);

            logger.LogInformation("Created operator={OperatorId} email={Email}", entity.Id, entity.Email);
            await auditService.LogAsync("CreateOperator", "Operator", entity.Id.ToString(),
                $"Created operator '{entity.DisplayName}' ({entity.Email}), system={entity.IsSystemOperator}");

            return new CreateOperatorResult(true, entity.Id, null);
        }
        catch (Exception ex)
        {
            logger.LogError(ex, "Failed to create operator");
            return new CreateOperatorResult(false, null, "Failed to create operator");
        }
    }

    public async Task<OperatorActionResult> DeactivateOperator(Guid operatorId, CancellationToken ct = default)
    {
        try
        {
            await using var db = await dbFactory.CreateDbContextAsync(ct);

            var caller = OperatorRequestContext.Current;
            if (caller.OperatorId == operatorId)
                return new OperatorActionResult(false, "Cannot deactivate yourself");

            if (!await db.Operators.AnyAsync(o => o.Id == caller.OperatorId && !o.IsDeleted && o.IsSystemOperator, ct))
                return new OperatorActionResult(false, "Only system operators can deactivate other operators");

            var op = await db.Operators.FirstOrDefaultAsync(o => o.Id == operatorId && !o.IsDeleted, ct);
            if (op is null)
                return new OperatorActionResult(false, "Operator not found");

            if (!op.IsActive)
                return new OperatorActionResult(false, "Operator is already inactive");

            op.IsActive = false;
            await db.SaveChangesAsync(ct);

            logger.LogInformation("Operator={CallerId} deactivated operator={OperatorId}", caller.OperatorId, operatorId);
            await auditService.LogAsync("DeactivateOperator", "Operator", operatorId.ToString());
            return new OperatorActionResult(true, null);
        }
        catch (Exception ex)
        {
            logger.LogError(ex, "Failed to deactivate operator={OperatorId}", operatorId);
            return new OperatorActionResult(false, "Failed to deactivate operator");
        }
    }

    public async Task<OperatorActionResult> ActivateOperator(Guid operatorId, CancellationToken ct = default)
    {
        try
        {
            await using var db = await dbFactory.CreateDbContextAsync(ct);

            var caller = OperatorRequestContext.Current;

            if (!await db.Operators.AnyAsync(o => o.Id == caller.OperatorId && !o.IsDeleted && o.IsSystemOperator, ct))
                return new OperatorActionResult(false, "Only system operators can activate other operators");

            var op = await db.Operators.FirstOrDefaultAsync(o => o.Id == operatorId && !o.IsDeleted, ct);
            if (op is null)
                return new OperatorActionResult(false, "Operator not found");

            if (op.IsActive)
                return new OperatorActionResult(false, "Operator is already active");

            op.IsActive = true;
            await db.SaveChangesAsync(ct);

            logger.LogInformation("Operator={CallerId} activated operator={OperatorId}", caller.OperatorId, operatorId);
            await auditService.LogAsync("ActivateOperator", "Operator", operatorId.ToString());
            return new OperatorActionResult(true, null);
        }
        catch (Exception ex)
        {
            logger.LogError(ex, "Failed to activate operator={OperatorId}", operatorId);
            return new OperatorActionResult(false, "Failed to activate operator");
        }
    }

    public async Task<OperatorActionResult> RevokeOperatorCertificate(Guid certificateId, CancellationToken ct = default)
    {
        try
        {
            await using var db = await dbFactory.CreateDbContextAsync(ct);

            var caller = OperatorRequestContext.Current;

            var cert = await db.OperatorCertificates
               .AsNoTracking()
               .FirstOrDefaultAsync(c => c.Id == certificateId && !c.IsDeleted, ct);
            if (cert is null)
                return new OperatorActionResult(false, "Certificate not found");

            if (caller.OperatorId == cert.OperatorId)
                return new OperatorActionResult(false, "Cannot revoke your own certificate");

            if (!await db.Operators.AnyAsync(o => o.Id == caller.OperatorId && !o.IsDeleted && o.IsSystemOperator, ct))
                return new OperatorActionResult(false, "Only system operators can revoke certificates");

            await certificateService.RevokeCertificateAsync(certificateId);

            logger.LogInformation("Operator={CallerId} revoked certificate={CertificateId} for operator={OperatorId}",
                caller.OperatorId, certificateId, cert.OperatorId);
            await auditService.LogAsync("RevokeOperatorCertificate", "Operator", cert.OperatorId.ToString(),
                $"Revoked certificate {certificateId} (serial={cert.SerialNumber})");
            return new OperatorActionResult(true, null);
        }
        catch (InvalidOperationException ex)
        {
            return new OperatorActionResult(false, ex.Message);
        }
        catch (Exception ex)
        {
            logger.LogError(ex, "Failed to revoke certificate={CertificateId}", certificateId);
            return new OperatorActionResult(false, "Failed to revoke certificate");
        }
    }

    public async Task<EnrollCertificateResult> EnrollOperatorCertificate(Guid operatorId, string csrPem, string? deviceName, string? deviceSerialNumber, CancellationToken ct = default)
    {
        await using var db = await dbFactory.CreateDbContextAsync(ct);

        var caller = OperatorRequestContext.Current;
        if (!await db.Operators.AnyAsync(o => o.Id == caller.OperatorId && !o.IsDeleted && o.IsSystemOperator, ct))
            return new EnrollCertificateResult(false, null, null, null, null, null, null, "Only system operators can enroll certificates");

        var result = await certificateService.EnrollCertificateAsync(operatorId, csrPem, deviceName, deviceSerialNumber);

        if (result.IsSuccess)
        {
            var s = result.Value;
            await auditService.LogAsync("EnrollOperatorCertificate", "Operator", operatorId.ToString(),
                $"Enrolled certificate serial={s.SerialNumber}, device={s.Thumbprint}");
            return new EnrollCertificateResult(
                true,
                s.CertificateId,
                s.CertificatePem,
                s.CaChainPem,
                s.SerialNumber,
                s.Thumbprint,
                s.NotAfter.UtcDateTime,
                null);
        }

        var errorMessage = result.Error switch
        {
            EnrollmentError.OperatorNotFound  => "Operator not found",
            EnrollmentError.OperatorInactive  => "Operator is inactive",
            EnrollmentError.InvalidCsr        => "Invalid CSR format",
            EnrollmentError.CsrKeyTooSmall    => "CSR key size is too small (min RSA 2048, EC 256)",
            EnrollmentError.VaultSigningFailed => "Vault PKI signing failed",
            _                                 => "Unknown error"
        };

        return new EnrollCertificateResult(false, null, null, null, null, null, null, errorMessage);
    }

    // ===== Operator App Access =====

    public async Task<OperatorAppAccessList> GetOperatorAppAccess(Guid operatorId, CancellationToken ct = default)
    {
        await using var db = await dbFactory.CreateDbContextAsync(ct);

        var raw = await db.OperatorAppAccess
           .AsNoTracking()
           .Where(a => a.OperatorId == operatorId)
           .Join(db.AppEntities.AsNoTracking(),
                a => a.AppId,
                app => app.AppId,
                (a, app) => new
                {
                    a.OperatorId,
                    a.AppId,
                    AppName = app.Name,
                    app.ClientId,
                    a.AllowedScopes,
                    a.Claims,
                    a.GrantedBy,
                    a.GrantedAt,
                    a.IsActive
                })
           .ToListAsync(ct);

        var records = raw.Select(r => new OperatorAppAccessEntry(
            r.OperatorId,
            r.AppId,
            r.AppName,
            r.ClientId,
            new IonArray<string>(r.AllowedScopes),
            new IonArray<string>(r.Claims),
            r.GrantedBy,
            r.GrantedAt.UtcDateTime,
            r.IsActive
        )).ToList();

        return new OperatorAppAccessList(new IonArray<OperatorAppAccessEntry>(records));
    }

    public async Task<OperatorAppAccessResult> GrantOperatorAppAccess(GrantOperatorAppAccessInput input, CancellationToken ct = default)
    {
        try
        {
            await using var db = await dbFactory.CreateDbContextAsync(ct);

            var caller = OperatorRequestContext.Current;
            if (!await db.Operators.AnyAsync(o => o.Id == caller.OperatorId && !o.IsDeleted && o.IsSystemOperator, ct))
                return new OperatorAppAccessResult(false, "Only system operators can manage operator app access");

            var operatorExists = await db.Operators.AnyAsync(o => o.Id == input.operatorId && !o.IsDeleted, ct);
            if (!operatorExists)
                return new OperatorAppAccessResult(false, "Operator not found");

            var app = await db.AppEntities.AsNoTracking().FirstOrDefaultAsync(a => a.AppId == input.appId, ct);
            if (app is null)
                return new OperatorAppAccessResult(false, "Application not found");

            var existing = await db.OperatorAppAccess
               .FirstOrDefaultAsync(a => a.OperatorId == input.operatorId && a.AppId == input.appId, ct);

            if (existing is not null)
                return new OperatorAppAccessResult(false, "Access record already exists. Use UpdateOperatorAppAccess to modify it.");

            var entity = new OperatorAppAccessEntity
            {
                OperatorId    = input.operatorId,
                AppId         = input.appId,
                AllowedScopes = input.allowedScopes.Values.ToList(),
                Claims        = input.claims.Values.ToList(),
                GrantedBy     = caller.OperatorId,
                GrantedAt     = DateTimeOffset.UtcNow,
                IsActive      = true
            };

            db.OperatorAppAccess.Add(entity);
            await db.SaveChangesAsync(ct);

            logger.LogInformation("Operator={CallerId} granted app access: operator={OperatorId} app={AppId} ({AppName})",
                caller.OperatorId, input.operatorId, input.appId, app.Name);
            await auditService.LogAsync("GrantOperatorAppAccess", "OperatorAppAccess",
                $"{input.operatorId}:{input.appId}",
                $"Granted access to app '{app.Name}' (clientId={app.ClientId}), scopes=[{string.Join(",", input.allowedScopes)}], claims=[{string.Join(",", input.claims)}]");

            await InvalidateOperatorAppAccessCacheAsync(input.operatorId, input.appId);

            return new OperatorAppAccessResult(true, null);
        }
        catch (Exception ex)
        {
            logger.LogError(ex, "Failed to grant operator app access");
            return new OperatorAppAccessResult(false, "Failed to grant operator app access");
        }
    }

    public async Task<OperatorActionResult> RevokeOperatorAppAccess(Guid operatorId, Guid appId, CancellationToken ct = default)
    {
        try
        {
            await using var db = await dbFactory.CreateDbContextAsync(ct);

            var caller = OperatorRequestContext.Current;
            if (!await db.Operators.AnyAsync(o => o.Id == caller.OperatorId && !o.IsDeleted && o.IsSystemOperator, ct))
                return new OperatorActionResult(false, "Only system operators can manage operator app access");

            var record = await db.OperatorAppAccess
               .FirstOrDefaultAsync(a => a.OperatorId == operatorId && a.AppId == appId, ct);

            if (record is null)
                return new OperatorActionResult(false, "Access record not found");

            db.OperatorAppAccess.Remove(record);
            await db.SaveChangesAsync(ct);

            logger.LogInformation("Operator={CallerId} revoked app access: operator={OperatorId} app={AppId}",
                caller.OperatorId, operatorId, appId);
            await auditService.LogAsync("RevokeOperatorAppAccess", "OperatorAppAccess",
                $"{operatorId}:{appId}", "Revoked app access");

            await InvalidateOperatorAppAccessCacheAsync(operatorId, appId);

            return new OperatorActionResult(true, null);
        }
        catch (Exception ex)
        {
            logger.LogError(ex, "Failed to revoke operator app access");
            return new OperatorActionResult(false, "Failed to revoke operator app access");
        }
    }

    public async Task<OperatorAppAccessResult> UpdateOperatorAppAccess(UpdateOperatorAppAccessInput input, CancellationToken ct = default)
    {
        try
        {
            await using var db = await dbFactory.CreateDbContextAsync(ct);

            var caller = OperatorRequestContext.Current;
            if (!await db.Operators.AnyAsync(o => o.Id == caller.OperatorId && !o.IsDeleted && o.IsSystemOperator, ct))
                return new OperatorAppAccessResult(false, "Only system operators can manage operator app access");

            var record = await db.OperatorAppAccess
               .FirstOrDefaultAsync(a => a.OperatorId == input.operatorId && a.AppId == input.appId, ct);

            if (record is null)
                return new OperatorAppAccessResult(false, "Access record not found");

            record.AllowedScopes = input.allowedScopes.Values.ToList();
            record.Claims        = input.claims.Values.ToList();
            record.IsActive      = input.isActive;

            await db.SaveChangesAsync(ct);

            logger.LogInformation("Operator={CallerId} updated app access: operator={OperatorId} app={AppId} active={IsActive}",
                caller.OperatorId, input.operatorId, input.appId, input.isActive);
            await auditService.LogAsync("UpdateOperatorAppAccess", "OperatorAppAccess",
                $"{input.operatorId}:{input.appId}",
                $"Updated: scopes=[{string.Join(",", input.allowedScopes)}], claims=[{string.Join(",", input.claims)}], active={input.isActive}");

            await InvalidateOperatorAppAccessCacheAsync(input.operatorId, input.appId);

            return new OperatorAppAccessResult(true, null);
        }
        catch (Exception ex)
        {
            logger.LogError(ex, "Failed to update operator app access");
            return new OperatorAppAccessResult(false, "Failed to update operator app access");
        }
    }

    public async Task<InternalAppSearchResult> SearchInternalApps(string query, CancellationToken ct = default)
    {
        if (string.IsNullOrWhiteSpace(query))
            return new InternalAppSearchResult(new IonArray<InternalAppInfo>([]));

        await using var db = await dbFactory.CreateDbContextAsync(ct);
        var normalizedQuery = query.Trim().ToLowerInvariant();

        // Try GUID first
        if (Guid.TryParse(query, out var appId))
        {
            var byId = await db.AppEntities
               .AsNoTracking()
               .Include(a => a.Team)
               .Where(a => a.AppId == appId && a.IsInternalApp && !a.IsDeleted)
               .FirstOrDefaultAsync(ct);

            if (byId is not null)
                return new InternalAppSearchResult(new IonArray<InternalAppInfo>([MapInternalApp(byId)]));
        }

        // Search by name, clientId, or bot username
        var byNameOrClient = await db.AppEntities
           .AsNoTracking()
           .Include(a => a.Team)
           .Where(a => a.IsInternalApp && !a.IsDeleted &&
                       (a.Name.ToLower().Contains(normalizedQuery) ||
                        a.ClientId.ToLower().Contains(normalizedQuery)))
           .Take(20)
           .ToListAsync(ct);

        // Also search by bot username
        var byBotUsername = await db.BotEntities
           .AsNoTracking()
           .Include(b => b.BotAsUser)
           .Include(b => b.Team)
           .Where(b => b.IsInternalApp && !b.IsDeleted &&
                       b.BotAsUser.NormalizedUsername.Contains(normalizedQuery))
           .Take(20)
           .ToListAsync(ct);

        var results = byNameOrClient
           .Select(MapInternalApp)
           .Concat(byBotUsername.Select(b => MapInternalApp((DevAppEntity)b)))
           .DistinctBy(x => x.appId)
           .ToList();

        return new InternalAppSearchResult(new IonArray<InternalAppInfo>(results));
    }

    private static InternalAppInfo MapInternalApp(DevAppEntity app) => new(
        app.AppId,
        app.Name,
        app.ClientId,
        app.Description,
        (AdminDevAppType)(int)app.AppType,
        app.TeamId,
        app.Team?.Name ?? "",
        app.IsInternalApp
    );

    public async Task<AuditLogPage> GetAuditLog(AuditLogQuery query, CancellationToken ct = default)
    {
        var page = Math.Max(0, query.page);
        var pageSize = Math.Clamp(query.pageSize, 1, 100);

        var (entries, totalCount) = await auditService.QueryAsync(
            query.operatorId, query.action, query.targetId,
            query.fromDate,
            query.toDate,
            page, pageSize, ct);

        var mapped = entries.Select(MapAuditEntry).ToList();

        return new AuditLogPage(
            new IonArray<AuditEntry>(mapped),
            totalCount,
            page,
            pageSize
        );
    }

    // ===== Bot Management =====

    public async Task<AdminBotSearchResult> SearchBot(string query, CancellationToken ct = default)
    {
        if (string.IsNullOrWhiteSpace(query))
            return new AdminBotSearchResult(false, null);

        await using var db = await dbFactory.CreateDbContextAsync(ct);
        var normalizedQuery = query.Trim().ToLowerInvariant();

        // Try GUID first
        if (Guid.TryParse(query, out var appId))
        {
            var bot = await db.BotEntities
               .Include(b => b.BotAsUser)
               .Include(b => b.Team)
               .FirstOrDefaultAsync(b => b.AppId == appId, ct);

            if (bot is not null)
                return new AdminBotSearchResult(true, MapBotSummary(bot));
        }

        // Search by bot username or name
        var byName = await db.BotEntities
           .Include(b => b.BotAsUser)
           .Include(b => b.Team)
           .FirstOrDefaultAsync(b =>
                b.BotAsUser.NormalizedUsername == normalizedQuery ||
                b.Name.ToLower() == normalizedQuery, ct);

        return byName is not null
            ? new AdminBotSearchResult(true, MapBotSummary(byName))
            : new AdminBotSearchResult(false, null);
    }

    public async Task<AdminBotCard> GetBotCard(Guid appId, CancellationToken ct = default)
    {
        await using var db = await dbFactory.CreateDbContextAsync(ct);

        var bot = await db.BotEntities
                     .Include(b => b.BotAsUser)
                     .Include(b => b.Team).ThenInclude(t => t.Owner)
                     .FirstOrDefaultAsync(b => b.AppId == appId, ct)
                  ?? throw new InvalidOperationException("Bot not found");

        // Installed spaces
        var botMemberships = await db.UsersToServerRelations
           .Where(sm => sm.UserId == bot.BotAsUserId && !sm.IsDeleted)
           .Include(sm => sm.Space)
           .ToListAsync(ct);

        var installedSpaces = new List<AdminBotSpaceInfo>();
        foreach (var sm in botMemberships)
        {
            var memberCount = await db.UsersToServerRelations.CountAsync(x => x.SpaceId == sm.SpaceId && !x.IsDeleted, ct);

            // Check archetype for entitlements
            var botArchetype = await db.Archetypes
               .Where(a => a.SpaceId == sm.SpaceId && a.IsLocked && !a.IsDeleted)
               .Join(db.MemberArchetypes.Where(ma => ma.SpaceMemberId == sm.Id),
                    a => a.Id, ma => ma.ArchetypeId, (a, _) => a)
               .FirstOrDefaultAsync(ct);

            installedSpaces.Add(new AdminBotSpaceInfo(
                sm.SpaceId,
                sm.Space.Name,
                sm.Space.AvatarFileId,
                memberCount,
                (ArgonEntitlement)(ulong)(botArchetype?.Entitlement ?? ArgonEntitlement.None),
                botArchetype is not null && (ulong)botArchetype.Entitlement != (ulong)bot.RequiredEntitlements
            ));
        }

        // Commands
        var commands = await db.BotCommands
           .Where(c => c.AppId == appId)
           .Select(c => new AdminBotCommandInfo(
                c.CommandId,
                c.Name,
                c.Description,
                c.Options != null ? c.Options.Count : 0,
                c.SpaceId == null
            ))
           .ToListAsync(ct);

        var team = MapTeamSummary(bot.Team);
        var creator = new AdminUserSummary(
            bot.Team.Owner.Id,
            bot.Team.Owner.Username,
            bot.Team.Owner.DisplayName,
            bot.Team.Owner.AvatarFileId
        );

        return new AdminBotCard(
            bot.AppId,
            bot.Name,
            bot.BotAsUser.Username,
            bot.Description,
            bot.BotAsUser.AvatarFileId,
            bot.IsVerified,
            bot.IsPublic,
            bot.IsInternalApp,
            (AdminBotLifecycleState)(int)bot.LifecycleState,
            bot.MaxSpaces,
            botMemberships.Count,
            (ArgonEntitlement)(ulong)bot.RequiredEntitlements,
            new IonArray<string>(bot.RequiredScopes),
            bot.CreatedAt.UtcDateTime,
            team,
            creator,
            new IonArray<AdminBotSpaceInfo>(installedSpaces),
            new IonArray<AdminBotCommandInfo>(commands)
        );
    }

    public async Task<UserActionResult> SetBotVerified(Guid appId, bool isVerified, CancellationToken ct = default)
    {
        try
        {
            await using var db = await dbFactory.CreateDbContextAsync(ct);
            var bot = await db.BotEntities.FirstOrDefaultAsync(b => b.AppId == appId, ct);
            if (bot is null) return new UserActionResult(false, "Bot not found");

            bot.IsVerified = isVerified;
            await db.SaveChangesAsync(ct);
            await auditService.LogAsync("SetBotVerified", "Bot", appId.ToString(), $"IsVerified={isVerified}");
            return new UserActionResult(true, null);
        }
        catch (Exception ex) { return new UserActionResult(false, ex.Message); }
    }

    public async Task<UserActionResult> SetBotMaxSpaces(Guid appId, int maxSpaces, CancellationToken ct = default)
    {
        try
        {
            await using var db = await dbFactory.CreateDbContextAsync(ct);
            var bot = await db.BotEntities.FirstOrDefaultAsync(b => b.AppId == appId, ct);
            if (bot is null) return new UserActionResult(false, "Bot not found");

            bot.MaxSpaces = maxSpaces;
            await db.SaveChangesAsync(ct);
            await auditService.LogAsync("SetBotMaxSpaces", "Bot", appId.ToString(), $"MaxSpaces={maxSpaces}");
            return new UserActionResult(true, null);
        }
        catch (Exception ex) { return new UserActionResult(false, ex.Message); }
    }

    public async Task<UserActionResult> SetBotInternalApp(Guid appId, bool isInternalApp, CancellationToken ct = default)
    {
        try
        {
            await using var db = await dbFactory.CreateDbContextAsync(ct);
            var app = await db.AppEntities.FirstOrDefaultAsync(a => a.AppId == appId, ct);
            if (app is null) return new UserActionResult(false, "App not found");

            app.IsInternalApp = isInternalApp;
            await db.SaveChangesAsync(ct);
            await auditService.LogAsync("SetBotInternalApp", "App", appId.ToString(), $"IsInternalApp={isInternalApp}");
            return new UserActionResult(true, null);
        }
        catch (Exception ex) { return new UserActionResult(false, ex.Message); }
    }

    public async Task<UserActionResult> SetBotLifecycleState(Guid appId, AdminBotLifecycleState state, CancellationToken ct = default)
    {
        try
        {
            await using var db = await dbFactory.CreateDbContextAsync(ct);
            var bot = await db.BotEntities.FirstOrDefaultAsync(b => b.AppId == appId, ct);
            if (bot is null) return new UserActionResult(false, "Bot not found");

            bot.LifecycleState = (BotLifecycleState)(int)state;
            await db.SaveChangesAsync(ct);
            await auditService.LogAsync("SetBotLifecycleState", "Bot", appId.ToString(), $"State={state}");
            return new UserActionResult(true, null);
        }
        catch (Exception ex) { return new UserActionResult(false, ex.Message); }
    }

    // ===== Team Management =====

    public async Task<AdminTeamSearchResult> SearchTeam(string query, CancellationToken ct = default)
    {
        if (string.IsNullOrWhiteSpace(query))
            return new AdminTeamSearchResult(false, null);

        await using var db = await dbFactory.CreateDbContextAsync(ct);

        // Try GUID first
        if (Guid.TryParse(query, out var teamId))
        {
            var team = await db.TeamEntities
               .Include(t => t.Members)
               .Include(t => t.Applications)
               .FirstOrDefaultAsync(t => t.TeamId == teamId && !t.IsDeleted, ct);

            if (team is not null)
                return new AdminTeamSearchResult(true, MapTeamSummary(team));
        }

        // Search by name
        var normalizedQuery = query.Trim().ToLowerInvariant();
        var byName = await db.TeamEntities
           .Include(t => t.Members)
           .Include(t => t.Applications)
           .FirstOrDefaultAsync(t => !t.IsDeleted && t.Name.ToLower() == normalizedQuery, ct);

        return byName is not null
            ? new AdminTeamSearchResult(true, MapTeamSummary(byName))
            : new AdminTeamSearchResult(false, null);
    }

    public async Task<AdminTeamCard> GetTeamCard(Guid teamId, CancellationToken ct = default)
    {
        await using var db = await dbFactory.CreateDbContextAsync(ct);

        var team = await db.TeamEntities
                      .Include(t => t.Owner)
                      .Include(t => t.Members).ThenInclude(m => m.User)
                      .Include(t => t.Applications)
                      .FirstOrDefaultAsync(t => t.TeamId == teamId && !t.IsDeleted, ct)
                   ?? throw new InvalidOperationException("Team not found");

        var owner = new AdminUserSummary(
            team.Owner.Id,
            team.Owner.Username,
            team.Owner.DisplayName,
            team.Owner.AvatarFileId
        );

        var members = team.Members.Select(m => new AdminTeamMemberInfo(
            m.UserId,
            m.User.Username,
            m.User.DisplayName,
            m.User.AvatarFileId,
            m.IsOwner,
            m.JoinedAt,
            new IonArray<string>(m.Claims ?? [])
        )).ToList();

        var apps = team.Applications.Select(a =>
        {
            var isVerified = a is BotEntity bot ? bot.IsVerified : a is ClientAppEntity client && client.IsVerified;
            return new AdminTeamAppInfo(
                a.AppId,
                a.Name,
                (AdminDevAppType)(int)a.AppType,
                a.IsInternalApp,
                isVerified,
                a.CreatedAt.UtcDateTime
            );
        }).ToList();

        return new AdminTeamCard(
            team.TeamId,
            team.Name,
            team.AvatarFileId,
            owner,
            team.CreatedAt.UtcDateTime,
            new IonArray<AdminTeamMemberInfo>(members),
            new IonArray<AdminTeamAppInfo>(apps)
        );
    }

    // ===== Space Management =====

    public async Task<AdminSpaceSearchResult> SearchSpace(string query, CancellationToken ct = default)
    {
        if (string.IsNullOrWhiteSpace(query))
            return new AdminSpaceSearchResult(false, null, SpaceSearchMatchKind.None);

        await using var db = await dbFactory.CreateDbContextAsync(ct);

        // Try GUID first
        if (Guid.TryParse(query, out var spaceId))
        {
            var space = await db.Spaces.FirstOrDefaultAsync(s => s.Id == spaceId && !s.IsDeleted, ct);
            if (space is not null)
            {
                var memberCount = await db.UsersToServerRelations.CountAsync(x => x.SpaceId == spaceId && !x.IsDeleted, ct);
                var channelCount = await db.Channels.CountAsync(c => c.SpaceId == spaceId && !c.IsDeleted, ct);
                return new AdminSpaceSearchResult(true,
                    new AdminSpaceSummary(space.Id, space.Name, space.AvatarFileId, memberCount, channelCount, space.CreatedAt.UtcDateTime),
                    SpaceSearchMatchKind.SpaceId);
            }
        }

        // Search by name
        var normalizedQuery = query.Trim().ToLowerInvariant();
        var byName = await db.Spaces
           .FirstOrDefaultAsync(s => !s.IsDeleted && s.Name.ToLower() == normalizedQuery, ct);

        if (byName is not null)
        {
            var memberCount = await db.UsersToServerRelations.CountAsync(x => x.SpaceId == byName.Id && !x.IsDeleted, ct);
            var channelCount = await db.Channels.CountAsync(c => c.SpaceId == byName.Id && !c.IsDeleted, ct);
            return new AdminSpaceSearchResult(true,
                new AdminSpaceSummary(byName.Id, byName.Name, byName.AvatarFileId, memberCount, channelCount, byName.CreatedAt.UtcDateTime),
                SpaceSearchMatchKind.Name);
        }

        return new AdminSpaceSearchResult(false, null, SpaceSearchMatchKind.None);
    }

    public async Task<AdminSpaceCard> GetSpaceCard(Guid spaceId, CancellationToken ct = default)
    {
        await using var db = await dbFactory.CreateDbContextAsync(ct);

        var space = await db.Spaces
                       .Include(s => s.Channels.Where(c => !c.IsDeleted))
                       .Include(s => s.ChannelGroups.Where(g => !g.IsDeleted))
                       .Include(s => s.Archetypes.Where(a => !a.IsDeleted))
                       .FirstOrDefaultAsync(s => s.Id == spaceId && !s.IsDeleted, ct)
                    ?? throw new InvalidOperationException("Space not found");

        var creator = await db.Users
           .Where(u => u.Id == space.CreatorId)
           .Select(u => new AdminUserSummary(u.Id, u.Username, u.DisplayName, u.AvatarFileId))
           .FirstOrDefaultAsync(ct) ?? new AdminUserSummary(space.CreatorId, "Unknown", "Unknown", null);

        var memberCount = await db.UsersToServerRelations.CountAsync(x => x.SpaceId == spaceId && !x.IsDeleted, ct);

        // The channel's last-message id is no longer on the channel row — ChannelEntity.LastMessageId
        // is a column nothing writes any more — so it comes from ChannelLastMessages, one seek by
        // space. The Redis cell would be up to a flush interval fresher and is not consulted: an
        // operator looking at a space card is not reading unread state, and a support answer that
        // disagrees with the database by three seconds is worse than one that is the database.
        // A channel with no row has had nothing posted in it, and shows zero, which is what the
        // column said for such a channel too.
        var storedMarks = await db.ChannelLastMessages
           .AsNoTracking()
           .Where(m => m.SpaceId == spaceId)
           .Select(m => new { m.ChannelId, m.LastMessageId })
           .ToDictionaryAsync(m => m.ChannelId, m => m.LastMessageId, ct);

        var channels = space.Channels.Select(c => new AdminChannelInfo(
            c.Id,
            c.Name,
            (ChannelType)(int)c.ChannelType,
            c.Description,
            c.ChannelGroupId,
            c.SlowMode.HasValue ? (int)c.SlowMode.Value.TotalSeconds : null,
            c.DoNotRestrictBoosters,
            storedMarks.GetValueOrDefault(c.Id)
        )).ToList();

        var channelGroups = space.ChannelGroups.Select(g => new AdminChannelGroupInfo(
            g.Id,
            g.Name,
            g.Description,
            space.Channels.Count(c => c.ChannelGroupId == g.Id)
        )).ToList();

        // Archetype member counts
        var archetypeMemberCounts = await db.MemberArchetypes
           .Where(ma => space.Archetypes.Select(a => a.Id).Contains(ma.ArchetypeId))
           .GroupBy(ma => ma.ArchetypeId)
           .Select(g => new { g.Key, Count = g.Count() })
           .ToDictionaryAsync(x => x.Key, x => x.Count, ct);

        var archetypes = space.Archetypes.Select(a => new AdminArchetypeInfo(
            a.Id,
            a.Name,
            (ArgonEntitlement)(ulong)a.Entitlement,
            a.IsDefault,
            a.IsLocked,
            a.IsHidden,
            archetypeMemberCounts.GetValueOrDefault(a.Id, 0)
        )).ToList();

        // Installed bots
        var botMembers = await db.UsersToServerRelations
           .Where(sm => sm.SpaceId == spaceId && !sm.IsDeleted)
           .Join(db.Users.Where(u => u.BotEntityId != null),
                sm => sm.UserId, u => u.Id, (sm, u) => new { sm, u })
           .Join(db.BotEntities,
                x => x.u.BotEntityId, b => b.AppId, (x, b) => new { x.sm, x.u, b })
           .ToListAsync(ct);

        var installedBots = new List<AdminSpaceBotInfo>();
        foreach (var bm in botMembers)
        {
            var botArchetype = await db.Archetypes
               .Where(a => a.SpaceId == spaceId && a.IsLocked && !a.IsDeleted)
               .Join(db.MemberArchetypes.Where(ma => ma.SpaceMemberId == bm.sm.Id),
                    a => a.Id, ma => ma.ArchetypeId, (a, _) => a)
               .FirstOrDefaultAsync(ct);

            installedBots.Add(new AdminSpaceBotInfo(
                bm.b.AppId,
                bm.b.Name,
                bm.u.Username,
                bm.u.AvatarFileId,
                bm.b.IsVerified,
                (ArgonEntitlement)(ulong)(botArchetype?.Entitlement ?? ArgonEntitlement.None),
                botArchetype is not null && (ulong)botArchetype.Entitlement != (ulong)bm.b.RequiredEntitlements
            ));
        }

        // Recent invites (last 10)
        var invites = await db.Invites
           .Where(i => i.SpaceId == spaceId)
           .OrderByDescending(i => i.CreatedAt)
           .Take(10)
           .ToListAsync(ct);

        var issuerIds = invites.Select(i => i.CreatorId).Distinct().ToList();
        var issuerNames = await db.Users
           .Where(u => issuerIds.Contains(u.Id))
           .ToDictionaryAsync(u => u.Id, u => u.Username, ct);

        var recentInvites = invites.Select(i => new AdminInviteInfo(
            i.Id.ToString(),
            i.CreatorId,
            issuerNames.GetValueOrDefault(i.CreatorId, "Unknown"),
            i.ExpireAt.UtcDateTime,
            0
        )).ToList();

        return new AdminSpaceCard(
            space.Id,
            space.Name,
            space.Description,
            space.AvatarFileId,
            space.TopBannedFileId,
            space.IsCommunity,
            space.BoostCount,
            space.BoostLevel,
            creator,
            space.CreatedAt.UtcDateTime,
            memberCount,
            space.Channels.Count,
            botMembers.Count,
            new IonArray<AdminChannelInfo>(channels),
            new IonArray<AdminChannelGroupInfo>(channelGroups),
            new IonArray<AdminArchetypeInfo>(archetypes),
            new IonArray<AdminSpaceBotInfo>(installedBots),
            new IonArray<AdminInviteInfo>(recentInvites),
            space.IsVerified,
            space.IsOfficial
        );
    }

    public Task<UserActionResult> SetSpaceCommunity(Guid spaceId, bool isCommunity, CancellationToken ct = default)
        => SetSpaceFlag(spaceId, isCommunity: isCommunity, isOfficial: null,
            "SetSpaceCommunity", $"IsCommunity={isCommunity}", ct);

    public Task<UserActionResult> SetSpaceOfficial(Guid spaceId, bool isOfficial, CancellationToken ct = default)
        => SetSpaceFlag(spaceId, isCommunity: null, isOfficial: isOfficial,
            "SetSpaceOfficial", $"IsOfficial={isOfficial}", ct);

    /// <summary>
    /// Both space-flag buttons: check the space is really there, hand the flip to the grain (which
    /// owns the write, the cache drop and the broadcast), then audit it.
    /// </summary>
    private async Task<UserActionResult> SetSpaceFlag(Guid spaceId, bool? isCommunity, bool? isOfficial,
        string auditAction, string auditDetails, CancellationToken ct)
    {
        try
        {
            await using var db = await dbFactory.CreateDbContextAsync(ct);
            var exists = await db.Spaces.AnyAsync(s => s.Id == spaceId && !s.IsDeleted, ct);
            if (!exists) return new UserActionResult(false, "Space not found");

            await grainFactory.GetGrain<ISpaceGrain>(spaceId).SetPlatformSpaceFlags(isCommunity, isOfficial, ct);
            await auditService.LogAsync(auditAction, "Space", spaceId.ToString(), auditDetails);
            return new UserActionResult(true, null);
        }
        catch (Exception ex) { return new UserActionResult(false, ex.Message); }
    }

    public async Task<AdminSpaceMemberPage> GetSpaceMembers(Guid spaceId, int offset, int limit, CancellationToken ct = default)
    {
        await using var db = await dbFactory.CreateDbContextAsync(ct);

        offset = Math.Max(0, offset);
        limit = Math.Clamp(limit, 1, 100);

        var totalCount = await db.UsersToServerRelations
           .CountAsync(sm => sm.SpaceId == spaceId && !sm.IsDeleted, ct);

        var memberEntities = await db.UsersToServerRelations
           .Where(sm => sm.SpaceId == spaceId && !sm.IsDeleted)
           .OrderBy(sm => sm.CreatedAt)
           .Skip(offset)
           .Take(limit)
           .Include(sm => sm.User)
           .Include(sm => sm.SpaceMemberArchetypes).ThenInclude(sma => sma.Archetype)
           .ToListAsync(ct);

        var members = memberEntities.Select(sm => new AdminSpaceMemberInfo(
            sm.UserId,
            sm.User.Username,
            sm.User.DisplayName,
            sm.User.AvatarFileId,
            sm.CreatedAt.UtcDateTime,
            new IonArray<string>(sm.SpaceMemberArchetypes.Select(sma => sma.Archetype.Name).ToList())
        )).ToList();

        return new AdminSpaceMemberPage(
            new IonArray<AdminSpaceMemberInfo>(members),
            totalCount,
            offset,
            limit
        );
    }

    // ===== Premium Management =====

    public async Task<UserActionResult> CancelUserSubscription(Guid userId, CancellationToken ct = default)
    {
        try
        {
            var grain = grainFactory.GetGrain<IUltimaGrain>(userId);
            var result = await grain.CancelSubscriptionAsync(ct);
            if (!result)
                return new UserActionResult(false, "No active subscription to cancel");
            await auditService.LogAsync("CancelUserSubscription", "User", userId.ToString());
            return new UserActionResult(true, null);
        }
        catch (Exception ex) { return new UserActionResult(false, ex.Message); }
    }

    public async Task<UserActionResult> ExpireUserSubscription(Guid userId, CancellationToken ct = default)
    {
        try
        {
            var grain = grainFactory.GetGrain<IUltimaGrain>(userId);
            await grain.ExpireSubscriptionAsync(ct);
            await auditService.LogAsync("ExpireUserSubscription", "User", userId.ToString());
            return new UserActionResult(true, null);
        }
        catch (Exception ex) { return new UserActionResult(false, ex.Message); }
    }

    public async Task<UserActionResult> GrantPremium(Guid userId, UltimaPlan tier, int durationDays, CancellationToken ct = default)
    {
        try
        {
            var grain = grainFactory.GetGrain<IUltimaGrain>(userId);
            await grain.ActivateSubscriptionAsync((UltimaTier)(int)tier, durationDays, null, null, ct);
            await auditService.LogAsync("GrantPremium", "User", userId.ToString(), $"Tier={tier}, Days={durationDays}");
            return new UserActionResult(true, null);
        }
        catch (Exception ex) { return new UserActionResult(false, ex.Message); }
    }

    // ===== Payment/Transaction API =====

    public async Task<AdminTransactionPage> GetUserTransactions(Guid userId, int page, int pageSize, CancellationToken ct = default)
    {
        await using var db = await dbFactory.CreateDbContextAsync(ct);

        page = Math.Max(0, page);
        pageSize = Math.Clamp(pageSize, 1, 100);

        var totalCount = await db.PaymentTransactions.CountAsync(t => t.UserId == userId, ct);

        var username = await db.Users
           .Where(u => u.Id == userId)
           .Select(u => u.Username)
           .FirstOrDefaultAsync(ct) ?? "Unknown";

        // DateTimeOffset.UtcDateTime has no SQL translation. Projecting it inside the query made EF
        // throw while compiling the shaper, so this endpoint failed for every caller regardless of
        // whether the user had any transactions. Materialise first, convert after.
        var rows = await db.PaymentTransactions
           .Where(t => t.UserId == userId)
           .OrderByDescending(t => t.CreatedAt)
           .Skip(page * pageSize)
           .Take(pageSize)
           .ToListAsync(ct);

        var transactions = rows
           .Select(t => new AdminTransactionInfo(
                t.Id,
                t.UserId,
                username,
                t.XsollaTxId,
                t.TransactionType,
                t.PlanExternalId,
                t.BoostPackType,
                t.BoostCount,
                t.Amount,
                t.Currency,
                t.RecipientId,
                t.CardSuffix,
                t.CardBrand,
                t.Status,
                t.CreatedAt.UtcDateTime
            ))
           .ToList();

        return new AdminTransactionPage(
            new IonArray<AdminTransactionInfo>(transactions),
            totalCount,
            page,
            pageSize
        );
    }

    public async Task<AdminTransactionDetails?> GetTransactionByXsollaId(string xsollaTxId, CancellationToken ct = default)
    {
        await using var db = await dbFactory.CreateDbContextAsync(ct);

        var tx = await db.PaymentTransactions
           .Include(t => t.User)
           .FirstOrDefaultAsync(t => t.XsollaTxId == xsollaTxId, ct);

        if (tx is null)
            return null;

        var transactionInfo = new AdminTransactionInfo(
            tx.Id,
            tx.UserId,
            tx.User.Username,
            tx.XsollaTxId,
            tx.TransactionType,
            tx.PlanExternalId,
            tx.BoostPackType,
            tx.BoostCount,
            tx.Amount,
            tx.Currency,
            tx.RecipientId,
            tx.CardSuffix,
            tx.CardBrand,
            tx.Status,
            tx.CreatedAt.UtcDateTime
        );

        // Related items granted around the same time
        var relatedItems = await db.Items
           .Where(i => i.OwnerId == tx.UserId && !i.IsReference &&
                        i.CreatedAt >= tx.CreatedAt.AddMinutes(-1) &&
                        i.CreatedAt <= tx.CreatedAt.AddMinutes(5))
           .Select(i => new AdminTransactionItemInfo(i.Id, i.TemplateId, i.CreatedAt.UtcDateTime))
           .ToListAsync(ct);

        // Premium info
        AdminPremiumInfo? premiumInfo = null;
        var subscription = await db.UltimaSubscriptions
           .Where(s => s.UserId == tx.UserId)
           .OrderByDescending(s => s.StartsAt)
           .FirstOrDefaultAsync(ct);

        if (subscription is not null)
        {
            var usedBoostSlots = await db.SpaceBoosts
               .CountAsync(b => b.SubscriptionId == subscription.Id && b.SpaceId != null, ct);
            premiumInfo = new AdminPremiumInfo(
                subscription.Id,
                (UltimaPlan)(int)subscription.Tier,
                (UltimaSubscriptionStatus)(int)subscription.Status,
                subscription.StartsAt.UtcDateTime,
                subscription.ExpiresAt.UtcDateTime,
                subscription.AutoRenew,
                subscription.BoostSlots,
                usedBoostSlots,
                subscription.CancelledAt?.UtcDateTime,
                subscription.XsollaSubscriptionId,
                subscription.ActivatedFromItemId
            );
        }

        return new AdminTransactionDetails(
            transactionInfo,
            new IonArray<AdminTransactionItemInfo>(relatedItems),
            premiumInfo
        );
    }

    // ===== User Settings Mutations =====

    public async Task<UserActionResult> ChangeUserAuthMode(Guid userId, ArgonAuthMode authMode, CancellationToken ct = default)
    {
        try
        {
            await using var db = await dbFactory.CreateDbContextAsync(ct);
            var user = await db.Users.FirstOrDefaultAsync(u => u.Id == userId, ct);
            if (user is null) return new UserActionResult(false, "User not found");

            user.PreferredAuthMode = authMode;
            await db.SaveChangesAsync(ct);
            await auditService.LogAsync("ChangeUserAuthMode", "User", userId.ToString(), $"AuthMode={authMode}");
            return new UserActionResult(true, null);
        }
        catch (Exception ex) { return new UserActionResult(false, ex.Message); }
    }

    public async Task<UserActionResult> ChangeUserOtpMethod(Guid userId, OtpMethod otpMethod, CancellationToken ct = default)
    {
        try
        {
            await using var db = await dbFactory.CreateDbContextAsync(ct);
            var user = await db.Users.FirstOrDefaultAsync(u => u.Id == userId, ct);
            if (user is null) return new UserActionResult(false, "User not found");

            user.PreferredOtpMethod = otpMethod;
            await db.SaveChangesAsync(ct);
            await auditService.LogAsync("ChangeUserOtpMethod", "User", userId.ToString(), $"OtpMethod={otpMethod}");
            return new UserActionResult(true, null);
        }
        catch (Exception ex) { return new UserActionResult(false, ex.Message); }
    }

    public async Task<IUploadFileResult> BeginUploadUserAvatar(Guid userId, CancellationToken ct = default)
    {
        try
        {
            var userGrain = grainFactory.GetGrain<IUserGrain>(userId);
            var result = await userGrain.BeginUploadUserFile(UserFileKind.Avatar, ct);

            if (result.IsSuccess)
            {
                var t = result.Value;
                var formFields = new IonArray<FormField>(
                    t.Fields.Select(kv => new FormField(kv.Key, kv.Value)).ToList());
                return new SuccessUploadFile(t.BlobId, t.Url, formFields, t.TtlSeconds);
            }
            return new FailedUploadFile(result.Error);
        }
        catch (Exception)
        {
            return new FailedUploadFile(UploadFileError.INTERNAL_ERROR);
        }
    }

    public async Task<UserActionResult> CompleteUploadUserAvatar(Guid userId, Guid blobId, CancellationToken ct = default)
    {
        try
        {
            var userGrain = grainFactory.GetGrain<IUserGrain>(userId);
            await userGrain.CompleteUploadUserFile(blobId, UserFileKind.Avatar, ct);
            await auditService.LogAsync("ChangeUserAvatar", "User", userId.ToString());
            return new UserActionResult(true, null);
        }
        catch (Exception ex) { return new UserActionResult(false, ex.Message); }
    }

    // ===== Private Helpers =====

    private static AdminBotSummary MapBotSummary(BotEntity bot) => new(
        bot.AppId,
        bot.Name,
        bot.BotAsUser.Username,
        bot.Description,
        bot.BotAsUser.AvatarFileId,
        bot.IsVerified,
        bot.IsPublic,
        bot.TeamId,
        bot.Team.Name
    );

    private static AdminTeamSummary MapTeamSummary(DevTeamEntity team) => new(
        team.TeamId,
        team.Name,
        team.AvatarFileId,
        team.OwnerId,
        team.Members?.Count ?? 0,
        team.Applications?.Count ?? 0,
        team.CreatedAt.UtcDateTime
    );

    private static OperatorInfo MapOperatorInfo(OperatorEntity op)
    {
        var certificates = (op.Certificates ?? new List<OperatorCertificateEntity>())
           .OrderByDescending(c => c.CreatedAt)
           .Select(MapOperatorCertificateInfo)
           .ToList();

        return new OperatorInfo(
            op.Id,
            op.DisplayName,
            op.Email,
            op.UserId,
            op.IsActive,
            op.IsSystemOperator,
            new IonArray<OperatorCertificateInfo>(certificates),
            op.LastAuthAt?.UtcDateTime,
            op.CreatedAt.UtcDateTime
        );
    }

    private static OperatorCertificateInfo MapOperatorCertificateInfo(OperatorCertificateEntity c)
        => new(
            c.Id,
            c.SerialNumber,
            c.Thumbprint,
            c.Subject,
            c.NotBefore.UtcDateTime,
            c.NotAfter.UtcDateTime,
            c.NotAfter < DateTimeOffset.UtcNow,
            c.RevokedAt.HasValue,
            c.CreatedAt.UtcDateTime,
            c.DeviceName,
            c.DeviceSerialNumber
        );

    private static AuditEntry MapAuditEntry(OperatorAuditEntity a)
        => new(
            a.Id,
            a.OperatorId,
            a.OperatorEmail,
            a.Action,
            a.TargetType,
            a.TargetId,
            a.Details,
            a.CreatedAt.UtcDateTime
        );

    /// <summary>
    /// Invalidate Aegis-side HybridCache entries for operator app access.
    /// Keys match CachedTeamsRepository patterns; clears Redis L2 immediately,
    /// L1 on Aegis side expires within LocalCacheExpiration (1 min).
    /// </summary>
    private async Task InvalidateOperatorAppAccessCacheAsync(Guid operatorId, Guid appId)
    {
        await lockdownCache.RemoveAsync($"aegis:operator:app-access:{operatorId}:{appId}");
        await lockdownCache.RemoveAsync($"aegis:operator:has-app-access:{operatorId}");
    }

    // ===== Reports & Trust =====
    //
    // The console does not read the report tables. Every call goes through IReportGrain, the one
    // place the report system's rules live; what is left here is the mapping onto the console
    // contract, the audit line, and the operator's id — which the grain cannot see for itself,
    // since an operator's identity lives in a context that does not cross a grain call.

    private IReportGrain Reports => grainFactory.GetGrain<IReportGrain>(Guid.CreateVersion7());

    private static Guid CurrentOperatorId => OperatorRequestContext.Current.OperatorId;

    public async Task<AdminReportPage> GetReports(ReportStatus? status, ReportCategory? category, int limit, int offset, CancellationToken ct = default)
    {
        var page = await Reports.GetReportsAsync(new ReportCaseQuery(status, category, limit, offset), ct);

        return new AdminReportPage(
            new IonArray<AdminReportEntry>(page.Reports.Select(ToEntry).ToList()),
            page.TotalCount,
            page.Offset,
            page.Limit);
    }

    public async Task<AdminReportEntry> GetReportById(Guid reportId, CancellationToken ct = default)
    {
        var report = await Reports.GetReportAsync(reportId, ct)
                  ?? throw new KeyNotFoundException($"Report {reportId} not found");

        return ToEntry(report);
    }

    /// <summary>Resolves the report's case, and with it every report on that case.</summary>
    public async Task<UserActionResult> ResolveReport(ResolveReportInput input, CancellationToken ct = default)
    {
        var caseId = await Reports.FindCaseByReportAsync(input.reportId, ct);

        if (caseId is null)
            return new UserActionResult(false, "Report not found");

        return await ResolveReportCase(new ResolveReportCaseInput(caseId.Value, input.status, input.resolutionNote, input.applyAction), ct);
    }

    public async Task<UserActionResult> AssignReport(Guid reportId, Guid operatorId, CancellationToken ct = default)
    {
        var caseId = await Reports.FindCaseByReportAsync(reportId, ct);

        if (caseId is null)
            return new UserActionResult(false, "Report not found");

        return await AssignReportCase(caseId.Value, operatorId, ct);
    }

    public async Task<AdminReportCasePage> GetReportCases(ReportStatus? status, ReportCategory? category, int limit, int offset, CancellationToken ct = default)
    {
        var page = await Reports.GetCasesAsync(new ReportCaseQuery(status, category, limit, offset), ct);

        return new AdminReportCasePage(
            new IonArray<AdminReportCaseSummary>(page.Cases.Select(ToSummary).ToList()),
            page.TotalCount,
            page.Offset,
            page.Limit);
    }

    public async Task<AdminReportCaseDetails> GetReportCase(Guid caseId, CancellationToken ct = default)
    {
        var view = await Reports.GetCaseAsync(caseId, ct)
                ?? throw new KeyNotFoundException($"Report case {caseId} not found");

        return new AdminReportCaseDetails(
            ToSummary(view.Summary),
            view.ContentSnapshot,
            new IonArray<AdminReportEntry>(view.Reports.Select(ToEntry).ToList()),
            view.ResolutionNote,
            view.ResolvedByOperatorId,
            view.TargetTrustScore);
    }

    public async Task<UserActionResult> AssignReportCase(Guid caseId, Guid operatorId, CancellationToken ct = default)
    {
        var result = await Reports.AssignCaseAsync(caseId, operatorId, ct);

        if (result.Success)
            await auditService.LogAsync("AssignReportCase", "ReportCase", caseId.ToString(), $"OperatorId={operatorId}");

        return new UserActionResult(result.Success, result.Error);
    }

    public async Task<UserActionResult> ResolveReportCase(ResolveReportCaseInput input, CancellationToken ct = default)
    {
        var result = await Reports.ResolveCaseAsync(
            new ResolveReportCaseCommand(input.caseId, input.status, input.resolutionNote, input.applyAction, CurrentOperatorId), ct);

        if (result.Success)
            await auditService.LogAsync("ResolveReportCase", "ReportCase", input.caseId.ToString(),
                $"Status={input.status}, Action={input.applyAction}");

        return new UserActionResult(result.Success, result.Error);
    }

    public async Task<UserActionResult> ReopenReportCase(Guid caseId, string? note, CancellationToken ct = default)
    {
        var result = await Reports.ReopenCaseAsync(caseId, CurrentOperatorId, note, ct);

        if (result.Success)
            await auditService.LogAsync("ReopenReportCase", "ReportCase", caseId.ToString(), note);

        return new UserActionResult(result.Success, result.Error);
    }

    public async Task<AdminUserTrustCard> GetUserTrustCard(Guid userId, CancellationToken ct = default)
    {
        await using var db = await dbFactory.CreateDbContextAsync(ct);

        var trustGrain = grainFactory.GetGrain<IUserTrustGrain>(userId);
        var trustInfo = await trustGrain.GetTrustScoreAsync(ct);

        var user = await db.Users.AsNoTracking().FirstOrDefaultAsync(u => u.Id == userId, ct);
        var trustEntity = await db.UserTrustScores.AsNoTracking().FirstOrDefaultAsync(t => t.UserId == userId, ct);

        var accountAge = user is not null
            ? DateTimeOffset.UtcNow - user.CreatedAt
            : TimeSpan.Zero;

        var lastActivity = user?.UpdatedAt.UtcDateTime ?? DateTime.UtcNow;

        return new AdminUserTrustCard(
            userId,
            user?.Username ?? "unknown",
            trustInfo.trustScore,
            trustInfo.totalReportsReceived,
            trustInfo.confirmedReportsReceived,
            trustInfo.totalReportsFiled,
            trustInfo.falseReportsFiled,
            trustEntity?.AutoActionsApplied ?? 0,
            accountAge,
            lastActivity
        );
    }

    public async Task<AdminUserTrustCard> RecalculateUserTrust(Guid userId, CancellationToken ct = default)
    {
        var trustGrain = grainFactory.GetGrain<IUserTrustGrain>(userId);
        var trustInfo = await trustGrain.RecalculateTrustAsync(ct);

        await using var db = await dbFactory.CreateDbContextAsync(ct);
        var user = await db.Users.AsNoTracking().FirstOrDefaultAsync(u => u.Id == userId, ct);

        var accountAge = user is not null
            ? DateTimeOffset.UtcNow - user.CreatedAt
            : TimeSpan.Zero;

        await auditService.LogAsync("RecalculateUserTrust", "User", userId.ToString(),
            $"NewScore={trustInfo.trustScore}");

        var trustEntity = await db.UserTrustScores.AsNoTracking().FirstOrDefaultAsync(t => t.UserId == userId, ct);

        return new AdminUserTrustCard(
            userId,
            user?.Username ?? "unknown",
            trustInfo.trustScore,
            trustInfo.totalReportsReceived,
            trustInfo.confirmedReportsReceived,
            trustInfo.totalReportsFiled,
            trustInfo.falseReportsFiled,
            trustEntity?.AutoActionsApplied ?? 0,
            accountAge,
            user?.UpdatedAt.UtcDateTime ?? DateTime.UtcNow
        );
    }

    private static AdminReportEntry ToEntry(ReportEntryView r)
        => new(
            r.ReportId,
            r.ReporterId,
            r.ReporterUsername,
            new ReportTarget(r.TargetKind, r.TargetId, r.ChannelId, r.MessageId is { } message ? (ulong)message : null),
            r.TargetDisplayName,
            r.Category,
            r.Reason,
            r.AdditionalInfo,
            r.Status,
            null,
            r.AssignedOperatorId,
            r.ResolutionNote,
            r.CreatedAt,
            r.ResolvedAt,
            r.CaseId,
            r.PriorityScore,
            r.EscalationRule,
            r.IsIndependent);

    private static AdminReportCaseSummary ToSummary(ReportCaseSummary c)
        => new(
            c.CaseId,
            new ReportTarget(c.TargetKind, c.TargetId, c.ChannelId, c.MessageId is { } message ? (ulong)message : null),
            c.TargetDisplayName,
            c.Status,
            c.TopCategory,
            c.PriorityScore,
            c.ReportCount,
            c.IndependentReporterCount,
            c.IsEscalated,
            c.EscalationRule,
            c.AssignedOperatorId,
            c.FirstReportedAt,
            c.LastReportedAt,
            c.ResolvedAt,
            c.AppliedAction);

    #region Inactivity deletion queue

    // The console's half of the account-retention rework. The daily sweep used to call
    // RequestAutoDeleteAsync itself and erase dormant accounts on a timer; it now writes candidates into
    // IAccountDeletionQueueGrain and the decision is made here, by a person, with an audit line behind it.
    // Nothing in this section deletes anything: approving forwards to the same guarded grain call a
    // person's own deletion request goes through, so lockdown, an active subscription, an owned space and
    // a standing refusal all still bar it.

    private IAccountDeletionQueueGrain DeletionQueue
        => grainFactory.GetGrain<IAccountDeletionQueueGrain>(IAccountDeletionQueueGrain.SingletonId);

    /// <summary>
    /// One page of the queue, joined against the accounts it names.
    /// </summary>
    /// <remarks>
    /// <para>The grain stores ids and the scan's arithmetic and nothing else — a name or an address
    /// written into grain state would be a copy that goes stale and a second place personal data lives.
    /// The display fields are read here, on the way out.</para>
    ///
    /// <para><b>Read past the soft-delete filter, and no row is ever dropped.</b> Defect R21: every
    /// <c>ArgonEntity</c> carries the global <c>!IsDeleted</c> filter, and step three of an erasure sets
    /// exactly that flag — so an approved account disappeared from this page the moment its deletion
    /// started running, which is the window the entry exists for. The page rendered nine rows over a
    /// total of ten, paging drifted by the number of erasures in flight, and an operator looking for the
    /// outcome of their own approval found nothing at all. A row whose account cannot be read is now
    /// rendered with blanks rather than skipped: an entry with no user behind it is a fact an operator
    /// should see, not one to hide, and <c>TotalCount</c> stays the queue's own count because the page no
    /// longer disagrees with it.</para>
    /// </remarks>
    public async Task<AccountDeletionQueuePage> GetAccountDeletionQueue(int offset, int limit, CancellationToken ct = default)
    {
        var snapshot = await DeletionQueue.ListAsync(offset, limit);

        if (snapshot.Entries.Count == 0)
            return new AccountDeletionQueuePage(
                new IonArray<AccountDeletionQueueEntry>([]), snapshot.TotalCount, snapshot.Offset, snapshot.Limit);

        var accounts = await ReadQueuedAccountsAsync(snapshot.Entries.Select(entry => entry.UserId).ToList(), ct);

        var entries = snapshot.Entries
           .Select(entry =>
            {
                var account = accounts.GetValueOrDefault(entry.UserId);

                return new AccountDeletionQueueEntry(
                    entry.UserId,
                    account?.Username ?? "",
                    account?.DisplayName ?? "",
                    account?.Email ?? "",
                    entry.LastActivityAt,
                    entry.ThresholdMonths,
                    entry.Reason,
                    entry.EnqueuedAt,
                    StateOf(entry.State),
                    entry.DecidedByOperatorId,
                    entry.DecidedByOperatorEmail,
                    entry.DecidedAt,
                    entry.ScheduledDeletionAt,
                    entry.StrandedSince,
                    entry.CompletedAt);
            })
           .ToList();

        return new AccountDeletionQueuePage(
            new IonArray<AccountDeletionQueueEntry>(entries), snapshot.TotalCount, snapshot.Offset, snapshot.Limit);
    }

    /// <summary>How one entry's state reaches an operator's screen.</summary>
    /// <remarks>
    /// <para>Defect F9. This used to be <c>Pending ? PENDING : APPROVED</c>, and the wire enum had no
    /// third member to map to, so a <see cref="QueuedAccountDeletionState.Stranded"/> entry — an
    /// erasure that started, gave up with steps left undone, and will be picked up by nobody — was
    /// rendered as an ordinary approval. <see cref="IAccountDeletionQueueGrain.ListAsync"/> takes care
    /// to sort those into the middle band precisely because they are work owed rather than work in
    /// progress, and the mapping then flattened the distinction away: an operator scanning the queue
    /// read "Approved" against erasures that had been stuck for days, with a non-null
    /// <c>strandedSince</c> as the only sign, and never opened the stranded page.</para>
    ///
    /// <para><c>AccountDeletionQueueEntryState</c> gained <c>STRANDED</c> for this. Appending to an ion
    /// enum is the safe direction — a reader that does not declare the member decodes it verbatim
    /// through the generated open-enum helpers rather than rejecting the message — and the operator
    /// console is regenerated with the schema in any case. An exhaustive switch here rather than a
    /// second ternary, so the next state the grain-side enum grows is a compile-time question and not
    /// another silent APPROVED.</para>
    ///
    /// <para>Which is what happened next: <c>COMPLETED</c>, the state an entry reaches when the erasure
    /// it authorised has run and it is being kept for <c>AccountDeletion:DecisionRetention</c> as the
    /// record of a decision that took effect. It is the same argument as F9 one step further along the
    /// lifecycle — an "Approved" badge over a finished erasure reads as one still inside its grace
    /// period, where the account holder can yet call it off — and it is the badge that makes the
    /// retention window legible, since the row is on the page for a reason an operator can otherwise
    /// only guess at.</para>
    /// </remarks>
    private static AccountDeletionQueueEntryState StateOf(QueuedAccountDeletionState state)
        => state switch
        {
            QueuedAccountDeletionState.Pending   => AccountDeletionQueueEntryState.PENDING,
            QueuedAccountDeletionState.Approved  => AccountDeletionQueueEntryState.APPROVED,
            QueuedAccountDeletionState.Stranded  => AccountDeletionQueueEntryState.STRANDED,
            QueuedAccountDeletionState.Completed => AccountDeletionQueueEntryState.COMPLETED,
            _                                    => throw new ArgumentOutOfRangeException(
                nameof(state), state, "the deletion queue grew a state the console has no badge for")
        };

    /// <summary>
    /// The erasures that gave up half way, with the identity of the account each one left behind.
    /// </summary>
    /// <remarks>
    /// <para>Defect R5. An erasure that spends its attempts unregisters its own poll and is never armed
    /// again, and everything that could have shown that state used to erase it: the account holder cannot
    /// sign in to ask (their password digest and address are gone by step three), the scan will never
    /// propose an anonymised row again, and the queue retired the entry as soon as the deletion stopped
    /// running. This is the page that keeps it, and <see cref="ResumeAccountDeletion"/> is the button on
    /// it.</para>
    ///
    /// <para>Every account here is a tombstone by definition, so the join reads past the soft-delete
    /// filter — the same reason <see cref="GetAccountDeletionQueue"/> does — and what it renders is
    /// "Deleted Account" and a <c>deleted_…</c> username. That is the point rather than a wart: the row
    /// exists to say which account is in this state, and its id is the handle.</para>
    /// </remarks>
    public async Task<StrandedAccountDeletionPage> GetStrandedAccountDeletions(int offset, int limit, CancellationToken ct = default)
    {
        var snapshot = await DeletionQueue.ListStrandedAsync(offset, limit);

        if (snapshot.Entries.Count == 0)
            return new StrandedAccountDeletionPage(
                new IonArray<StrandedAccountDeletion>([]), snapshot.TotalCount, snapshot.Offset, snapshot.Limit);

        var accounts = await ReadQueuedAccountsAsync(snapshot.Entries.Select(entry => entry.UserId).ToList(), ct);

        var entries = snapshot.Entries
           .Select(entry =>
            {
                var account = accounts.GetValueOrDefault(entry.UserId);

                return new StrandedAccountDeletion(
                    entry.UserId,
                    account?.Username ?? "",
                    account?.DisplayName ?? "",
                    account?.Email ?? "",
                    entry.DecidedByOperatorId,
                    entry.DecidedByOperatorEmail,
                    entry.DecidedAt,
                    entry.ScheduledDeletionAt,
                    entry.StrandedSince,
                    entry.FailureReason,
                    entry.ExecutionAttempts);
            })
           .ToList();

        return new StrandedAccountDeletionPage(
            new IonArray<StrandedAccountDeletion>(entries), snapshot.TotalCount, snapshot.Offset, snapshot.Limit);
    }

    /// <summary>The display fields of the accounts a queue page names, erased ones included.</summary>
    private async Task<Dictionary<Guid, QueuedAccountIdentity>> ReadQueuedAccountsAsync(List<Guid> ids, CancellationToken ct)
    {
        await using var db = await dbFactory.CreateDbContextAsync(ct);

        return await db.Users
           .IgnoreQueryFilters()
           .AsNoTracking()
           .Where(u => ids.Contains(u.Id))
           .Select(u => new QueuedAccountIdentity(u.Id, u.Username, u.DisplayName, u.Email))
           .ToDictionaryAsync(u => u.Id, ct);
    }

    private sealed record QueuedAccountIdentity(Guid Id, string Username, string DisplayName, string Email);

    /// <summary>
    /// Approves one queued account: the deletion is scheduled and the account is told by e-mail.
    /// </summary>
    /// <remarks>
    /// The refusal the deletion grain can still give is passed back verbatim rather than flattened into
    /// "failed": between the sweep that proposed the account and this click it may have been put under
    /// lockdown, bought a subscription or been left owning a space, and which of those it is decides what
    /// an operator does next.
    /// </remarks>
    public async Task<UserActionResult> ApproveAccountDeletion(Guid userId, CancellationToken ct = default)
    {
        // Written before the irreversible act, so the intent survives whatever happens next — defect R7.
        // The audit log is where an operator review looks, and it used to be written only after the queue
        // had already scheduled the erasure and mailed the account, only on the success path, and inside
        // the try whose catch reports a failure. An audit store that was briefly unavailable therefore
        // erased the one record of who ordered the deletion, and a refusal — an operator repeatedly
        // trying to approve a locked or space-owning account — was never recorded at all.
        await AuditQuietlyAsync("ApproveAccountDeletion", userId, "Outcome=attempted");

        try
        {
            var caller   = OperatorRequestContext.Current;
            var decision = await DeletionQueue.ApproveAsync(userId, caller.OperatorId, caller.Email);

            if (!decision.Success)
            {
                await AuditQuietlyAsync("ApproveAccountDeletion", userId,
                    $"Outcome=refused; Error={decision.Error}; RequestError={decision.RequestError}");

                return new UserActionResult(false, decision.Error switch
                {
                    AccountDeletionQueueDecisionError.NotQueued =>
                        "That account is not in the deletion queue",
                    AccountDeletionQueueDecisionError.AlreadyDecided =>
                        "That account has already been approved",
                    AccountDeletionQueueDecisionError.RefusedByDeletionGrain =>
                        $"Deletion refused: {decision.RequestError}",
                    _ => "Could not approve the deletion"
                });
            }

            // Outside the result-bearing path on purpose — defect R27. By here the deletion is scheduled,
            // the entry is Approved and the account has its notice mail; an audit write that throws must
            // not turn that into "failed" on the operator's screen, because their next click answers
            // "already approved" and they are left with two contradictory truths about an erasure that is
            // going to happen either way.
            await AuditQuietlyAsync("ApproveAccountDeletion", userId,
                $"Outcome=scheduled; ScheduledFor={decision.ScheduledDeletionAt:O}");

            return new UserActionResult(true, null);
        }
        catch (Exception ex)
        {
            logger.LogError(ex, "Failed to approve the queued deletion of {UserId}", userId);

            await AuditQuietlyAsync("ApproveAccountDeletion", userId, $"Outcome=error; {ex.Message}");

            return new UserActionResult(false, ex.Message);
        }
    }

    /// <summary>
    /// Writes an audit line and never lets its failure become the caller's.
    /// </summary>
    /// <remarks>
    /// The audit store being unavailable is exactly the moment you least want to lose a record, and also
    /// the moment when reporting its failure is most misleading: the operator's action either happened or
    /// it did not, and that is what the result has to say. A write that cannot land is logged at Critical
    /// — the application log is the last copy of it.
    /// </remarks>
    private async Task AuditQuietlyAsync(string action, Guid userId, string details)
    {
        try
        {
            await auditService.LogAsync(action, "User", userId.ToString(), details);
        }
        catch (Exception ex)
        {
            logger.LogCritical(ex,
                "Could not write the operator audit line for {Action} on {UserId} ({Details}); "
              + "this log line is the only remaining record of it", action, userId, details);
        }
    }

    /// <summary>
    /// Rejects one queued account: the entry goes, and the sweep leaves that account alone for a full
    /// inactivity period.
    /// </summary>
    /// <remarks>
    /// The hold is the point. An entry is a projection of the last sweep, so a rejection that only removed
    /// it would last until the next pass and the same account would be proposed again tomorrow — which is
    /// defect CON-4 (a refusal the system cannot remember) with an operator in the account holder's place.
    /// </remarks>
    public async Task<UserActionResult> RejectAccountDeletion(Guid userId, CancellationToken ct = default)
    {
        try
        {
            var caller   = OperatorRequestContext.Current;
            var decision = await DeletionQueue.RejectAsync(userId, caller.OperatorId, caller.Email);

            if (!decision.Success)
            {
                await AuditQuietlyAsync("RejectAccountDeletion", userId,
                    $"Outcome=refused; Error={decision.Error}");

                return new UserActionResult(false, decision.Error switch
                {
                    AccountDeletionQueueDecisionError.NotQueued =>
                        "That account is not in the deletion queue",
                    AccountDeletionQueueDecisionError.AlreadyDecided =>
                        "That deletion is already scheduled; it can only be cancelled by the account holder",
                    _ => "Could not reject the deletion"
                });
            }

            await AuditQuietlyAsync("RejectAccountDeletion", userId, "Outcome=declined");

            return new UserActionResult(true, null);
        }
        catch (Exception ex)
        {
            logger.LogError(ex, "Failed to reject the queued deletion of {UserId}", userId);

            await AuditQuietlyAsync("RejectAccountDeletion", userId, $"Outcome=error; {ex.Message}");

            return new UserActionResult(false, ex.Message);
        }
    }

    /// <summary>
    /// Picks a stranded erasure back up: the deletion resumes from the step it reached.
    /// </summary>
    /// <remarks>
    /// <para>The operator half of defect R5. The work itself is deliberately not awaited here — an
    /// erasure is minutes of database work and one grain call per space and per file, so
    /// <see cref="IAccountDeletionGrain.ResumeAsync"/> clears the attempt count, arms the poll to fire at
    /// once and answers with the status to render. Nothing is repeated: the deletion resumes from its own
    /// record of the steps it has already done, which matters because a released file reference cannot be
    /// released a second time.</para>
    ///
    /// <para>Audited attempt-and-outcome like an approval, and for the same reason: it finishes an
    /// irreversible act on somebody's account. It is not restricted to accounts the queue knows about —
    /// a person's own deletion request can strand in exactly the same way and has no queue entry at all,
    /// and refusing to let an operator reach that account would leave the only case nobody can see.</para>
    ///
    /// <para>The queue is told afterwards so the stranded page settles immediately instead of at the next
    /// daily pass; a queue that will not answer costs a stale row for a day and nothing else, so it never
    /// turns a resumed deletion into a reported failure.</para>
    /// </remarks>
    public async Task<UserActionResult> ResumeAccountDeletion(Guid userId, CancellationToken ct = default)
    {
        await AuditQuietlyAsync("ResumeAccountDeletion", userId, "Outcome=attempted");

        try
        {
            var status = await grainFactory.GetGrain<IAccountDeletionGrain>(userId).ResumeAsync();

            if (status.Status is AccountDeletionStatusKind.None or AccountDeletionStatusKind.Completed)
            {
                await AuditQuietlyAsync("ResumeAccountDeletion", userId, $"Outcome=nothingToResume; Status={status.Status}");

                return new UserActionResult(false, status.Status is AccountDeletionStatusKind.Completed
                    ? "That account's deletion has already finished"
                    : "That account has no deletion to resume");
            }

            await AuditQuietlyAsync("ResumeAccountDeletion", userId,
                $"Outcome=resumed; Status={status.Status}; Attempts={status.ExecutionAttempts}; "
              + $"LastError={status.FailureReason}");

            try
            {
                await DeletionQueue.RefreshAsync(userId);
            }
            catch (Exception ex)
            {
                logger.LogWarning(ex,
                    "Resumed the deletion of {UserId} but could not refresh its queue entry; the next scan will", userId);
            }

            return new UserActionResult(true, null);
        }
        catch (Exception ex)
        {
            logger.LogError(ex, "Failed to resume the stranded deletion of {UserId}", userId);

            await AuditQuietlyAsync("ResumeAccountDeletion", userId, $"Outcome=error; {ex.Message}");

            return new UserActionResult(false, ex.Message);
        }
    }


    /// <summary>The inactivity sweep as an operator sees it: when it last ran, what it did, when it runs next.</summary>
    /// <remarks>
    /// Read-only and unaudited, unlike everything else on this page — it names no account and changes
    /// nothing. It exists because an empty queue reads the same whether nothing was proposable or
    /// nothing ran, and telling those apart used to mean reading a pod's log.
    /// </remarks>
    public async Task<AutoDeleteScanStatus> GetAutoDeleteScanStatus(CancellationToken ct = default)
        => Map(await Scheduler.GetScanStatusAsync());

    /// <summary>Runs a pass now and answers with what it did.</summary>
    /// <remarks>
    /// Audited, though a pass only ever writes to the queue: what it costs is a full read of the user
    /// table, and an operator who can trigger that on demand is worth a row in the log. It runs whatever
    /// the switch says — the switch governs the timer, not the button — and it deliberately does not
    /// move the timer, so forcing one does not postpone tomorrow's.
    /// </remarks>
    public async Task<AutoDeleteScanStatus> RunAutoDeleteScan(CancellationToken ct = default)
    {
        await AuditQuietlyAsync("RunAutoDeleteScan", Guid.Empty, "Outcome=attempted");

        try
        {
            await Scheduler.RunScanAsync();

            var status = await Scheduler.GetScanStatusAsync();

            await AuditQuietlyAsync("RunAutoDeleteScan", Guid.Empty,
                $"Outcome=ran; Processed={status.LastProcessed}; Proposed={status.LastProposed}; "
              + $"Enqueued={status.LastEnqueued}; Retired={status.LastRetired}; Length={status.LastQueueLength}");

            return Map(status);
        }
        catch (Exception ex)
        {
            logger.LogError(ex, "An operator's forced inactivity scan failed");
            await AuditQuietlyAsync("RunAutoDeleteScan", Guid.Empty, $"Outcome=error; {ex.Message}");

            return Map(await Scheduler.GetScanStatusAsync());
        }
    }

    /// <summary>What the platform mailed, for a fortnight.</summary>
    /// <remarks>
    /// <para>The questions this answers — did the reset code go out, why did this person get a deletion
    /// notice — had no answer at all before: a send is a log line on whichever pod made it, and the log
    /// line is gone by the time anybody asks.</para>
    ///
    /// <para>It holds no address, no subject and no body. An account id, which template it was, when,
    /// and whether it left the building. That is enough to answer the question and not enough to be a
    /// record of who was mailed where.</para>
    /// </remarks>
    public async Task<EmailJournalPage> GetEmailJournal(Guid? userId, int offset, int limit, CancellationToken ct = default)
    {
        var page = await emailJournal.ReadAsync(userId, offset, limit, ct);

        var rows = page.Entries
           .Select(entry => new EmailJournalEntry(
                entry.UserId,
                entry.Kind,
                entry.SentAt.UtcDateTime,
                entry.Delivered,
                entry.Error))
           .ToList();

        return new EmailJournalPage(new IonArray<EmailJournalEntry>(rows), page.TotalCount, offset, limit);
    }

    /// <summary>The erasures under way right now, whoever started them.</summary>
    /// <remarks>
    /// <para>A deletion lives in one grain per account and nothing indexes those, so this reads the
    /// register the deletion grain writes as it arms, executes, finishes or is called off. Both kinds
    /// end up in it: an operator's approval and a person deleting their own account from the console,
    /// which never touches the queue at all and was previously invisible here.</para>
    ///
    /// <para>The names are read with <c>IgnoreQueryFilters</c> and may be blank. That is not a defect
    /// to paper over: the erasure anonymises the row on its third step of eleven, so an account halfway
    /// through genuinely no longer has a name or an address, and showing the id alone is the honest
    /// answer. The register's own copy of the status is trusted for the list — asking every grain would
    /// turn a page into a fan-out — and the impact panel re-reads the one account an operator opens.</para>
    /// </remarks>
    public async Task<InFlightDeletionPage> GetDeletionsInFlight(int offset, int limit, CancellationToken ct = default)
    {
        var snapshot = await DeletionQueue.ListInFlightAsync(offset, limit);

        if (snapshot.Entries.Count == 0)
            return new InFlightDeletionPage(new IonArray<InFlightDeletionEntry>([]),
                snapshot.TotalCount, snapshot.FailedCount, offset, limit);

        var ids = snapshot.Entries.Select(entry => entry.UserId).ToList();

        await using var ctx = await dbFactory.CreateDbContextAsync(ct);

        var accounts = await ctx.Users
           .IgnoreQueryFilters()
           .AsNoTracking()
           .Where(u => ids.Contains(u.Id))
           .Select(u => new { u.Id, u.Username, u.DisplayName, u.Email })
           .ToDictionaryAsync(u => u.Id, ct);

        var rows = new List<InFlightDeletionEntry>(snapshot.Entries.Count);

        foreach (var entry in snapshot.Entries)
        {
            accounts.TryGetValue(entry.UserId, out var account);

            // One grain call per row on the page, not per account in the register: the failure and the
            // attempt count are the two things an operator acts on and the register does not carry them.
            var status = await grainFactory.GetGrain<IAccountDeletionGrain>(entry.UserId).GetDeletionStatusAsync();

            // And the row that the register still believes in but the account does not. The register is
            // written one-way, so an update can be lost; the account's own grain is the fact, and a
            // deletion that is over does not belong on a list of work in progress.
            if (status.Status is AccountDeletionStatusKind.None or AccountDeletionStatusKind.Completed)
                continue;

            rows.Add(new InFlightDeletionEntry(
                entry.UserId,
                account?.Username ?? "",
                account?.DisplayName ?? "",
                account?.Email ?? "",
                status.Status switch
                {
                    AccountDeletionStatusKind.Scheduled => AccountDeletionStatusView.SCHEDULED,
                    AccountDeletionStatusKind.Executing => AccountDeletionStatusView.EXECUTING,
                    AccountDeletionStatusKind.Completed => AccountDeletionStatusView.COMPLETED,
                    AccountDeletionStatusKind.Failed    => AccountDeletionStatusView.FAILED,
                    _                                   => AccountDeletionStatusView.NONE
                },
                entry.ArmedAt.UtcDateTime,
                (status.ExecutionAt ?? entry.ExecutionAt)?.UtcDateTime,
                entry.SelfRequested,
                status.FailureReason,
                status.ExecutionAttempts));
        }

        return new InFlightDeletionPage(new IonArray<InFlightDeletionEntry>(rows),
            snapshot.TotalCount, snapshot.FailedCount, offset, limit);
    }

    /// <summary>What erasing one account would actually destroy.</summary>
    /// <remarks>
    /// <para>Read on demand rather than folded into the queue page: every count below is a query, and
    /// an operator wants them for the one account they are deciding about. The queue lists who and how
    /// long they have been quiet; this answers "and what goes with them", which is the question that
    /// decides whether an approval is routine or needs a conversation first.</para>
    ///
    /// <para>The dates are the sweep's own arithmetic spelled out — the later of the last login and the
    /// last message, the threshold that account is measured against, and whether it chose that
    /// threshold itself — so an operator can see why the account was proposed rather than trusting that
    /// it was. <see cref="AccountDeletionImpact.blockedBy"/> is the bar that would refuse a deletion
    /// asked for right now, which is not always the same as the entry being approvable: a queue entry
    /// is written by a pass that ran up to a day ago.</para>
    /// </remarks>
    public async Task<AccountDeletionImpact> GetAccountDeletionImpact(Guid userId, CancellationToken ct = default)
    {
        await using var ctx = await dbFactory.CreateDbContextAsync(ct);

        var account = await ctx.Users
           .AsNoTracking()
           .Where(u => u.Id == userId)
           .Select(u => new
            {
                u.Id,
                u.Username,
                u.DisplayName,
                u.Email,
                u.CreatedAt,
                u.HasActiveUltima,
                u.LockdownReason,
                u.LockDownExpiration,
                IsBot            = ctx.BotEntities.Any(bot => bot.BotAsUserId == u.Id),
                AutoDeleteOn     = ctx.AutoDeleteSettings.Where(x => x.UserId == u.Id).Select(x => (bool?)x.Enabled).FirstOrDefault(),
                AutoDeleteMonths = ctx.AutoDeleteSettings.Where(x => x.UserId == u.Id).Select(x => x.Months).FirstOrDefault(),
                LastLogin        = ctx.DeviceHistories.Where(d => d.UserId == u.Id).Max(d => (DateTimeOffset?)d.LastLoginTime),
                LastMessage      = ctx.Messages.Where(m => m.CreatorId == u.Id).Max(m => (DateTimeOffset?)m.CreatedAt),
                Memberships      = ctx.UsersToServerRelations.Count(m => m.UserId == u.Id && !m.IsDeleted),
                Messages         = ctx.Messages.Count(m => m.CreatorId == u.Id && !m.IsDeleted),
                Files            = ctx.Files.Count(f => f.OwnerId == u.Id && !f.IsDeleted),
                Conversations    = ctx.UserConversations.Count(c => c.UserId == u.Id),
                BotsOwned        = ctx.BotEntities.Count(b => !b.IsDeleted
                                    && ctx.TeamEntities.Any(t => t.TeamId == b.TeamId && t.OwnerId == u.Id))
            })
           .FirstOrDefaultAsync(ct);

        if (account is null)
            return Empty(userId);

        // Named rather than counted: "three spaces will be deleted" is a number an operator has to take
        // on trust, and the names are what let them recognise the one that should not have been there.
        var owned = await ctx.Spaces
           .AsNoTracking()
           .Where(space => space.CreatorId == userId && !space.IsDeleted)
           .Select(space => new { space.Name, space.IsCommunity })
           .ToListAsync(ct);

        var deleted     = owned.Where(space => !space.IsCommunity).Select(space => space.Name).ToArray();
        var communities = owned.Where(space => space.IsCommunity).Select(space => space.Name).ToArray();

        var status = await grainFactory.GetGrain<IAccountDeletionGrain>(userId).GetDeletionStatusAsync();

        var chosen          = account.AutoDeleteOn is true && account.AutoDeleteMonths is > 0;
        var thresholdMonths = chosen
            ? account.AutoDeleteMonths!.Value
            : deletionOptions.Value.DefaultInactivityMonths;

        var lastActivity = (account.LastLogin, account.LastMessage) switch
        {
            ({ } login, { } message) => login > message ? login : message,
            ({ } login, null)        => login,
            (null, { } message)      => message,
            _                        => (DateTimeOffset?)null
        };

        var locked = account.LockdownReason != LockdownReason.NONE
                  && (account.LockDownExpiration is not { } expiry || expiry > DateTimeOffset.UtcNow);

        // The order the grain checks them in, so the answer is the one it would actually give.
        var blockedBy = account.IsBot || userId == UserEntity.SystemUser ? "bot or platform account"
            : locked                                                    ? "standing lockdown"
            : account.HasActiveUltima                                   ? "active subscription"
            : communities.Length > 0                                    ? "owns a community"
            : status.Status is not AccountDeletionStatusKind.None        ? $"deletion already {status.Status}"
            : null;

        return new AccountDeletionImpact(
            userId, true, account.Username, account.DisplayName, account.Email,
            account.CreatedAt.UtcDateTime,
            lastActivity?.UtcDateTime,
            account.LastLogin?.UtcDateTime,
            account.LastMessage?.UtcDateTime,
            thresholdMonths,
            chosen,
            account.HasActiveUltima,
            locked,
            account.IsBot,
            deleted.Length, new IonArray<string>(deleted),
            communities.Length, new IonArray<string>(communities),
            account.Memberships,
            account.Messages,
            account.Files,
            account.Conversations,
            account.BotsOwned,
            status.Status switch
            {
                AccountDeletionStatusKind.Scheduled => AccountDeletionStatusView.SCHEDULED,
                AccountDeletionStatusKind.Executing => AccountDeletionStatusView.EXECUTING,
                AccountDeletionStatusKind.Completed => AccountDeletionStatusView.COMPLETED,
                AccountDeletionStatusKind.Failed    => AccountDeletionStatusView.FAILED,
                _                                   => AccountDeletionStatusView.NONE
            },
            status.ScheduledAt?.UtcDateTime,
            status.ExecutionAt?.UtcDateTime,
            blockedBy);

        static AccountDeletionImpact Empty(Guid id)
            => new(id, false, "", "", "", default, null, null, null, 0, false, false, false, false,
                0, new IonArray<string>([]), 0, new IonArray<string>([]), 0, 0, 0, 0, 0,
                AccountDeletionStatusView.NONE, null, null, "no such account");
    }

    /// <summary>Starts the deletion workflow on an account nobody proposed.</summary>
    /// <remarks>
    /// The countdown an approval arms, reached without a queue entry: the ordinary grace period, the
    /// notice mail now, and an account that can still call it off from its own console or by signing in.
    /// Every bar stands. Not restricted to dormant accounts on purpose — the sweep decides what is
    /// dormant, an operator decides what needs deleting, and support tickets are the second kind.
    /// </remarks>
    public Task<UserActionResult> StartAccountDeletion(Guid userId, CancellationToken ct = default)
        => DriveDeletionAsync("StartAccountDeletion", userId, grain => grain.StartByOperatorAsync());

    /// <summary>Brings an armed erasure forward to now.</summary>
    /// <remarks>
    /// Refused unless something is already counting down: this button shortens a decision, it does not
    /// take one. The erasure runs on the poll that follows, which is armed to fire immediately.
    /// </remarks>
    public Task<UserActionResult> ExpireAccountDeletionGrace(Guid userId, CancellationToken ct = default)
        => DriveDeletionAsync("ExpireAccountDeletionGrace", userId, grain => grain.ExpireGraceAsync());

    /// <summary>Erases an account immediately, with no mail to it at all.</summary>
    /// <remarks>
    /// The last-resort button: no grace, no notice, no confirmation, and the erasure runs inside this
    /// call rather than on the next poll, so the answer says whether it actually finished. The bars still
    /// stand — they are the platform's invariants, not a courtesy to the account holder.
    /// </remarks>
    public Task<UserActionResult> EraseAccountNow(Guid userId, CancellationToken ct = default)
        => DriveDeletionAsync("EraseAccountNow", userId, grain => grain.EraseNowAsync());

    /// <summary>The three operator-driven deletion buttons, which differ only in which call they make.</summary>
    /// <remarks>
    /// Audited attempt-then-outcome like the queue's own decisions, and for the same reason: each one is
    /// a step towards an irreversible act on somebody's account, and the row that says who asked has to
    /// survive whether or not the call succeeded.
    /// </remarks>
    private async Task<UserActionResult> DriveDeletionAsync(
        string action, Guid userId, Func<IAccountDeletionGrain, ValueTask<AccountDeletionRequestResult>> drive)
    {
        await AuditQuietlyAsync(action, userId, "Outcome=attempted");

        try
        {
            var result = await drive(grainFactory.GetGrain<IAccountDeletionGrain>(userId));

            if (!result.Success)
            {
                await AuditQuietlyAsync(action, userId, $"Outcome=refused; Reason={result.Error}");
                return new UserActionResult(false, Explain(result.Error));
            }

            await AuditQuietlyAsync(action, userId, $"Outcome=done; ExecutionAt={result.ScheduledDeletionAt:O}");

            // So the queue's own page settles now rather than at the next pass; a queue that will not
            // answer costs a stale row and nothing else.
            try
            {
                await DeletionQueue.RefreshAsync(userId);
            }
            catch (Exception ex)
            {
                logger.LogWarning(ex,
                    "Drove the deletion of {UserId} but could not refresh its queue entry; the next scan will", userId);
            }

            return new UserActionResult(true, null);
        }
        catch (Exception ex)
        {
            logger.LogError(ex, "Operator action {Action} failed for {UserId}", action, userId);
            await AuditQuietlyAsync(action, userId, $"Outcome=error; {ex.Message}");
            return new UserActionResult(false, ex.Message);
        }
    }

    /// <summary>The grain's refusal, in words a console can show.</summary>
    private static string Explain(AccountDeletionRequestError? error)
        => error switch
        {
            AccountDeletionRequestError.AlreadyScheduled      => "That account's deletion is already under way",
            AccountDeletionRequestError.NotScheduled          => "That account has no deletion counting down",
            AccountDeletionRequestError.HasActiveSubscription => "That account has an active subscription",
            AccountDeletionRequestError.OwnsSpaces            => "That account still owns a space",
            AccountDeletionRequestError.AccountLocked         => "That account is under a standing lockdown",
            AccountDeletionRequestError.ServiceAccount        => "That is a bot or platform account and cannot be deleted",
            AccountDeletionRequestError.RecentlyDeclined      => "That account recently refused a deletion",
            AccountDeletionRequestError.InvalidPassword       => "The account's password was not accepted",
            _                                                 => "The server refused the request"
        };

    private IAutoDeleteSchedulerGrain Scheduler
        => grainFactory.GetGrain<IAutoDeleteSchedulerGrain>(IAutoDeleteSchedulerGrain.SingletonId);

    private static AutoDeleteScanStatus Map(AutoDeleteScanReport report)
        => new(report.Enabled,
            report.ArmedAt?.UtcDateTime,
            report.NextDueAt?.UtcDateTime,
            report.LastStartedAt?.UtcDateTime,
            report.LastFinishedAt?.UtcDateTime,
            report.LastTrigger,
            report.Runs,
            report.LastProcessed,
            report.LastProposed,
            report.LastEnqueued,
            report.LastRetired,
            report.LastHeld,
            report.LastQueueLength,
            report.LastError,
            report.LastErrorAt?.UtcDateTime,
            report.DefaultThresholdMonths);

    #endregion

    #region Feature Flags

    public async Task<FeatureFlagList> GetFeatureFlags(CancellationToken ct = default)
    {
        var grain = grainFactory.GetGrain<IFeatureFlagGrain>(Guid.Empty);
        var flags = await grain.ListFlagsAsync();

        var summaries = flags.Select(f => new FeatureFlagSummary(
            f.Id,
            f.Description,
            f.DefaultEnabled,
            f.RolloutPercentage,
            f.HasVariants,
            f.UssdActivationCode,
            f.ExpiresAt?.UtcDateTime,
            f.OverrideCount,
            f.CreatedAt.UtcDateTime)).ToList();

        return new FeatureFlagList(new IonArray<FeatureFlagSummary>(summaries));
    }

    public async Task<FeatureFlagDetails> GetFeatureFlag(string flagId, CancellationToken ct = default)
    {
        var grain = grainFactory.GetGrain<IFeatureFlagGrain>(Guid.Empty);
        var flag  = await grain.GetFlagAsync(flagId);
        if (flag is null)
            throw new InvalidOperationException($"Feature flag '{flagId}' not found");

        var overrides = flag.Overrides.Select(o => new FeatureFlagOverrideInfo(
            o.OverrideId,
            (int)o.Scope,
            o.TargetId,
            o.Enabled,
            o.RolloutPercentage,
            o.ForcedVariant,
            o.CreatedAt.UtcDateTime)).ToList();

        return new FeatureFlagDetails(
            flag.Id,
            flag.Description,
            flag.DefaultEnabled,
            flag.RolloutPercentage,
            flag.Variants,
            flag.UssdActivationCode,
            flag.ExpiresAt?.UtcDateTime,
            flag.CreatedAt.UtcDateTime,
            new IonArray<FeatureFlagOverrideInfo>(overrides));
    }

    public async Task<FeatureFlagActionResult> CreateFeatureFlag(CreateFeatureFlagInput input, CancellationToken ct = default)
    {
        var grain  = grainFactory.GetGrain<IFeatureFlagGrain>(Guid.Empty);
        var result = await grain.CreateFlagAsync(new FeatureFlagInput(
            input.flagId, input.description, input.defaultEnabled, input.rolloutPercentage,
            input.variants, input.ussdActivationCode, ToOffset(input.expiresAt)));

        if (result.Success)
            await auditService.LogAsync("CreateFeatureFlag", "FeatureFlag", input.flagId,
                $"Created feature flag '{input.flagId}', ussd={input.ussdActivationCode ?? "-"}");

        return new FeatureFlagActionResult(result.Success, result.FlagId, result.Error);
    }

    public async Task<FeatureFlagActionResult> UpdateFeatureFlag(UpdateFeatureFlagInput input, CancellationToken ct = default)
    {
        var grain  = grainFactory.GetGrain<IFeatureFlagGrain>(Guid.Empty);
        var result = await grain.UpdateFlagAsync(new FeatureFlagInput(
            input.flagId, input.description, input.defaultEnabled, input.rolloutPercentage,
            input.variants, input.ussdActivationCode, ToOffset(input.expiresAt)));

        if (result.Success)
            await auditService.LogAsync("UpdateFeatureFlag", "FeatureFlag", input.flagId,
                $"Updated feature flag '{input.flagId}'");

        return new FeatureFlagActionResult(result.Success, result.FlagId, result.Error);
    }

    public async Task<FeatureFlagActionResult> DeleteFeatureFlag(string flagId, CancellationToken ct = default)
    {
        var grain  = grainFactory.GetGrain<IFeatureFlagGrain>(Guid.Empty);
        var result = await grain.DeleteFlagAsync(flagId);

        if (result.Success)
            await auditService.LogAsync("DeleteFeatureFlag", "FeatureFlag", flagId, $"Deleted feature flag '{flagId}'");

        return new FeatureFlagActionResult(result.Success, result.FlagId, result.Error);
    }

    public async Task<FeatureFlagActionResult> SetFeatureFlagOverride(SetFeatureFlagOverrideInput input, CancellationToken ct = default)
    {
        var grain = grainFactory.GetGrain<IFeatureFlagGrain>(Guid.Empty);
        var scope = (FeatureFlagScope)input.scope;

        // User-scope enable routes through the activation path so the user gets the same realtime event.
        if (scope == FeatureFlagScope.User && input.enabled == true)
        {
            if (!Guid.TryParse(input.targetId, out var userId))
                return new FeatureFlagActionResult(false, input.flagId, "Target id must be a user GUID for User scope");

            var activation = await grain.ActivateForUserAsync(userId, input.flagId);
            if (!activation.IsEnabled)
                return new FeatureFlagActionResult(false, input.flagId, "Activation failed (flag missing or expired)");

            await NotifyUserAsync(userId, new FeatureFlagActivated(userId, input.flagId, true, activation.Variant));
            await auditService.LogAsync("SetFeatureFlagOverride", "FeatureFlag", input.flagId,
                $"Activated flag '{input.flagId}' for user {userId} (User scope)");

            return new FeatureFlagActionResult(true, input.flagId, null);
        }

        var result = await grain.SetOverrideAsync(new FeatureFlagOverrideInput(
            input.flagId, scope, input.targetId, input.enabled, input.rolloutPercentage, input.forcedVariant));

        if (result.Success)
            await auditService.LogAsync("SetFeatureFlagOverride", "FeatureFlag", input.flagId,
                $"Set override scope={scope} target={input.targetId} enabled={input.enabled}");

        return new FeatureFlagActionResult(result.Success, result.FlagId, result.Error);
    }

    public async Task<FeatureFlagActionResult> DeleteFeatureFlagOverride(Guid overrideId, CancellationToken ct = default)
    {
        var grain  = grainFactory.GetGrain<IFeatureFlagGrain>(Guid.Empty);
        var result = await grain.DeleteOverrideAsync(overrideId);

        if (result.Success)
            await auditService.LogAsync("DeleteFeatureFlagOverride", "FeatureFlag", result.FlagId,
                $"Deleted override {overrideId}");

        return new FeatureFlagActionResult(result.Success, result.FlagId, result.Error);
    }

    // ── Tenant directory (self-hosted / enterprise routing) ──────────────────────────────────

    public async Task<TenantDirectoryList> GetTenantDirectory(CancellationToken ct = default)
    {
        await using var db = await dbFactory.CreateDbContextAsync(ct);

        var entities = await db.TenantDirectory
           .Where(t => !t.IsDeleted)
           .OrderBy(t => t.Domain)
           .ToListAsync(ct);

        var tenants = entities.Select(t => new TenantInfo(
            t.Id, t.Domain, t.InstanceUrl, t.IsVerified, t.OrgName, t.OwnerUserId, t.Notes, t.CreatedAt.UtcDateTime)).ToList();

        return new TenantDirectoryList(new IonArray<TenantInfo>(tenants));
    }

    public async Task<TenantActionResult> CreateTenant(CreateTenantInput input, CancellationToken ct = default)
    {
        try
        {
            var domain = NormalizeTenantDomain(input.domain);
            if (domain is null)
                return new TenantActionResult(false, null, "A valid email domain is required");
            if (!IsValidInstanceUrl(input.instanceUrl))
                return new TenantActionResult(false, null, "Instance URL must be an absolute https URL");

            await using var db = await dbFactory.CreateDbContextAsync(ct);
            if (await db.TenantDirectory.AnyAsync(t => t.Domain == domain && !t.IsDeleted, ct))
                return new TenantActionResult(false, null, "A tenant for this domain already exists");

            var entity = new TenantDirectoryEntity
            {
                Domain      = domain,
                InstanceUrl = input.instanceUrl.Trim(),
                IsVerified  = false, // verify is a separate, system-operator-gated step
                OrgName     = input.orgName?.Trim(),
                OwnerUserId = input.ownerUserId,
                Notes       = input.notes?.Trim()
            };
            db.TenantDirectory.Add(entity);
            await db.SaveChangesAsync(ct);

            await auditService.LogAsync("CreateTenant", "Tenant", entity.Id.ToString(),
                $"Created tenant domain='{domain}' url='{entity.InstanceUrl}' (unverified)");
            return new TenantActionResult(true, entity.Id, null);
        }
        catch (Exception ex)
        {
            logger.LogError(ex, "Failed to create tenant");
            return new TenantActionResult(false, null, "Failed to create tenant");
        }
    }

    public async Task<TenantActionResult> UpdateTenant(UpdateTenantInput input, CancellationToken ct = default)
    {
        try
        {
            if (!IsValidInstanceUrl(input.instanceUrl))
                return new TenantActionResult(false, input.tenantId, "Instance URL must be an absolute https URL");

            await using var db = await dbFactory.CreateDbContextAsync(ct);
            var t = await db.TenantDirectory.FirstOrDefaultAsync(x => x.Id == input.tenantId && !x.IsDeleted, ct);
            if (t is null)
                return new TenantActionResult(false, input.tenantId, "Tenant not found");

            t.InstanceUrl = input.instanceUrl.Trim();
            t.OrgName     = input.orgName?.Trim();
            t.Notes       = input.notes?.Trim();
            await db.SaveChangesAsync(ct);

            await auditService.LogAsync("UpdateTenant", "Tenant", t.Id.ToString(), $"Updated tenant '{t.Domain}'");
            return new TenantActionResult(true, t.Id, null);
        }
        catch (Exception ex)
        {
            logger.LogError(ex, "Failed to update tenant={TenantId}", input.tenantId);
            return new TenantActionResult(false, input.tenantId, "Failed to update tenant");
        }
    }

    public async Task<TenantActionResult> SetTenantVerified(Guid tenantId, bool isVerified, CancellationToken ct = default)
    {
        try
        {
            await using var db = await dbFactory.CreateDbContextAsync(ct);

            var caller = OperatorRequestContext.Current;
            if (!await db.Operators.AnyAsync(o => o.Id == caller.OperatorId && !o.IsDeleted && o.IsSystemOperator, ct))
                return new TenantActionResult(false, tenantId, "Only system operators can verify tenants");

            var t = await db.TenantDirectory.FirstOrDefaultAsync(x => x.Id == tenantId && !x.IsDeleted, ct);
            if (t is null)
                return new TenantActionResult(false, tenantId, "Tenant not found");

            t.IsVerified = isVerified;
            await db.SaveChangesAsync(ct);

            await auditService.LogAsync("SetTenantVerified", "Tenant", tenantId.ToString(),
                $"Set verified={isVerified} for '{t.Domain}'");
            return new TenantActionResult(true, tenantId, null);
        }
        catch (Exception ex)
        {
            logger.LogError(ex, "Failed to set tenant verified={TenantId}", tenantId);
            return new TenantActionResult(false, tenantId, "Failed to set tenant verification");
        }
    }

    public async Task<TenantActionResult> DeleteTenant(Guid tenantId, CancellationToken ct = default)
    {
        try
        {
            await using var db = await dbFactory.CreateDbContextAsync(ct);
            var t = await db.TenantDirectory.FirstOrDefaultAsync(x => x.Id == tenantId && !x.IsDeleted, ct);
            if (t is null)
                return new TenantActionResult(false, tenantId, "Tenant not found");

            t.IsDeleted = true;
            t.DeletedAt = DateTimeOffset.UtcNow;
            await db.SaveChangesAsync(ct);

            await auditService.LogAsync("DeleteTenant", "Tenant", tenantId.ToString(), $"Deleted tenant '{t.Domain}'");
            return new TenantActionResult(true, tenantId, null);
        }
        catch (Exception ex)
        {
            logger.LogError(ex, "Failed to delete tenant={TenantId}", tenantId);
            return new TenantActionResult(false, tenantId, "Failed to delete tenant");
        }
    }

    private static string? NormalizeTenantDomain(string? domain)
    {
        if (string.IsNullOrWhiteSpace(domain)) return null;
        var d  = domain.Trim().ToLowerInvariant();
        var at = d.LastIndexOf('@');
        if (at >= 0) d = d[(at + 1)..]; // tolerate a full email being pasted in
        if (d.Length == 0 || !d.Contains('.') || d.Any(char.IsWhiteSpace)) return null;
        return d;
    }

    private static bool IsValidInstanceUrl(string? url)
    {
        if (string.IsNullOrWhiteSpace(url)) return false;
        if (!Uri.TryCreate(url.Trim(), UriKind.Absolute, out var uri)) return false;
        if (uri.Scheme == Uri.UriSchemeHttps) return true;
        // allow plain http only for localhost dev instances
        return uri.Scheme == Uri.UriSchemeHttp && uri.Host is "localhost" or "127.0.0.1";
    }

    /// <summary>
    /// Kept as an identity now that the contract speaks <c>DateTimeOffset</c> directly.
    /// </summary>
    /// <remarks>
    /// The call sites read as "convert on the way in", which is still the right shape for them to
    /// have — the conversion simply has nothing left to do since ion's <c>datetime</c> stopped being
    /// a <c>DateTime</c>.
    /// </remarks>
    private static DateTimeOffset? ToOffset(DateTimeOffset? dt) => dt;

    private async Task NotifyUserAsync<T>(Guid userId, T payload) where T : IArgonEvent
    {
        var sessions = await sessionDiscovery.GetUserSessionsAsync(userId);
        if (sessions.Count == 0)
            return;

        await sessionNotifier.NotifySessionsAsync(sessions, payload);
    }

    #endregion
}
