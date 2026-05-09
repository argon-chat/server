namespace Argon.Api.Features.AdminApi;

using Argon.Api.Entities.Data;
using Argon.Api.Features.AdminApi.Diagnostics;
using Argon.Api.Grains.Interfaces;
using ConsoleContracts;
using ion.runtime;
using Livekit.Server.Sdk.Dotnet;

public class AdminConsoleImpl(
    IGrainFactory grainFactory,
    ILogger<IAdminConsole> logger,
    IDbContextFactory<ApplicationDbContext> dbFactory,
    RuntimeDiagnosticsService runtimeDiagnostics,
    DatabaseDiagnosticsService databaseDiagnostics,
    KubernetesDiagnosticsService? kubernetesDiagnostics,
    NatsDiagnosticsService? natsDiagnostics,
    RedisDiagnosticsService? redisDiagnostics,
    OrleansDiagnosticsService? orleansDiagnostics
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
            (LockdownReason)(int)user.LockdownReason,
            user.LockDownExpiration?.UtcDateTime,
            user.LockDownIsAppealable,
            (ArgonAuthMode)(int)user.PreferredAuthMode,
            (OtpMethod)(int)user.PreferredOtpMethod,
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
            autoDeleteSettings
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
                    Key = Guid.NewGuid(),
                    Edition = input.templateId
                },
                ItemScenarioKind.QualifierBox when boxContentIds?.Count == 1 => new QualifierBox
                {
                    Key = Guid.NewGuid(),
                    ReferenceItemId = boxContentIds[0]
                },
                ItemScenarioKind.QualifierBox when boxContentIds?.Count > 1 => new MultipleQualifierBox
                {
                    Key = Guid.NewGuid(),
                    ReferenceItemIds = boxContentIds
                },
                ItemScenarioKind.Premium => new PremiumScenario
                {
                    Key = Guid.NewGuid(),
                    PlanId = ""
                },
                ItemScenarioKind.RedeemCode => new RedeemScenario
                {
                    Key = Guid.NewGuid(),
                    Code = "",
                    ServiceKey = ""
                },
                _ => null
            };

            var item = new ArgonItemEntity
            {
                Id = Guid.NewGuid(),
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
                Id = Guid.NewGuid(),
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

    public async Task<UserActionResult> BlockUser(Guid userId, LockdownReason reason, DateTime? expiration, bool isAppealable,
        CancellationToken ct = default)
    {
        try
        {
            await using var db = await dbFactory.CreateDbContextAsync(ct);

            var user = await db.Users.FirstOrDefaultAsync(u => u.Id == userId, ct);
            if (user is null)
                return new UserActionResult(false, "User not found");

            user.LockdownReason = reason;
            user.LockDownExpiration = expiration.HasValue ? new DateTimeOffset(expiration.Value, TimeSpan.Zero) : null;
            user.LockDownIsAppealable = isAppealable;

            await db.SaveChangesAsync(ct);
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

            return success
                ? new UserActionResult(true, null)
                : new UserActionResult(false, "Failed to grant item");
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
}