namespace Argon.Grains;

using Argon.Api.Entities.Data;
using Argon.Features.EF;
using Argon.Features.Email;
using Argon.Grains.Interfaces;
using ConsoleContracts;
using ion.runtime;
using Microsoft.EntityFrameworkCore.Metadata;
using Microsoft.Extensions.Caching.Hybrid;
using Npgsql;
using Orleans.Concurrency;

/// <inheritdoc cref="IAdminPlatformGrain"/>
[StatelessWorker]
public sealed class AdminPlatformGrain(
    IDbContextFactory<ApplicationDbContext> dbFactory,
    DatabaseProvider databaseProvider,
    HybridCache cache,
    IEmailJournal emailJournal,
    ILogger<AdminPlatformGrain> logger) : Grain, IAdminPlatformGrain
{
    /// <summary>
    /// Below this many rows by the optimizer's estimate a table is counted exactly: the scan is cheap
    /// there, and a small table is where an estimate is least trustworthy — or absent altogether until
    /// its first statistics are collected.
    /// </summary>
    private const long ExactCountBelow = 100_000;

    private static readonly HybridCacheEntryOptions PlatformStatsCache = new()
    {
        Expiration           = TimeSpan.FromMinutes(5),
        LocalCacheExpiration = TimeSpan.FromMinutes(1)
    };

    /// <summary>The platform dashboard, at most five minutes old.</summary>
    /// <remarks>
    /// <para>Nobody reads this more than once a minute, and computing it is the most expensive thing the
    /// console does — so it is cached, and what is left to compute is made cheaper in two ways.</para>
    ///
    /// <para><b>The big tables are not counted.</b> <c>COUNT(*)</c> has no shortcut on CockroachDB: it is
    /// a scan of every range of the table. The totals for users, spaces, channels and messages come from
    /// the optimizer's row estimate instead, which is instant and a few percent off at worst — and counts
    /// soft-deleted rows too, which a dashboard total can live with. A table the estimate calls small is
    /// still counted exactly, which is also what keeps a fresh database (and the test suite) exact.</para>
    ///
    /// <para><b>On CockroachDB it reads ten seconds in the past.</b> A read at the present timestamp
    /// raises the timestamp cache over every span it touches, so the month of <c>Messages</c> scanned
    /// below would push every concurrent write into that table past it — and a pushed serializable
    /// write is one that may have to retry with 40001. A historical read does not, and does not wait on
    /// anybody's intents either. A database younger than ten seconds has no tables at that timestamp, so
    /// that one case reads at the present instead.</para>
    /// </remarks>
    public async Task<PlatformStats> GetPlatformStatsAsync(CancellationToken ct = default)
        => await cache.GetOrCreateAsync("admin:platform-stats",
            async token => await ReadPlatformStatsAsync(token), PlatformStatsCache, cancellationToken: ct);

    private async Task<PlatformStats> ReadPlatformStatsAsync(CancellationToken ct)
    {
        // Outside the historical transaction: the estimates are catalogue metadata, not table data, and
        // have nothing to gain from reading in the past.
        Dictionary<IEntityType, long> estimates;
        await using (var db = await dbFactory.CreateDbContextAsync(ct))
            estimates = await db.EstimateRowCountsAsync(databaseProvider.Kind,
                [db.Users.EntityType, db.Spaces.EntityType, db.Channels.EntityType, db.Messages.EntityType], ct);

        if (databaseProvider.Kind is DatabaseProviderKind.CockroachDb)
        {
            try
            {
                return await ReadPlatformStatsAsync(estimates, historical: true, ct);
            }
            catch (PostgresException e) when (e.SqlState == PostgresErrorCodes.UndefinedTable)
            {
                logger.LogDebug("The schema is younger than the historical timestamp; reading the platform stats at present");
            }
        }

        return await ReadPlatformStatsAsync(estimates, historical: false, ct);
    }

    private async Task<PlatformStats> ReadPlatformStatsAsync(IReadOnlyDictionary<IEntityType, long> estimates, bool historical,
        CancellationToken ct)
    {
        await using var db = await dbFactory.CreateDbContextAsync(ct);

        // A user transaction has to run inside the retrying strategy, or EF refuses to start it. Every
        // query below is an untracked aggregate, so a retry has no state of the failed attempt to trip on.
        return await db.Database.CreateExecutionStrategy().ExecuteAsync(async token =>
        {
            await using var tx = historical
                ? await db.Database.BeginHistoricalReadAsync(databaseProvider.Kind, TimeSpan.FromSeconds(10), token)
                : null;

            var stats = await CountPlatformAsync(db, estimates, token);

            if (tx is not null)
                await tx.CommitAsync(token);

            return stats;
        }, ct);
    }

    private static async Task<PlatformStats> CountPlatformAsync(ApplicationDbContext db, IReadOnlyDictionary<IEntityType, long> estimates,
        CancellationToken ct)
    {
        async Task<long> TotalAsync<T>(DbSet<T> set) where T : class
            => estimates.TryGetValue(set.EntityType, out var rows) && rows >= ExactCountBelow
                ? rows
                : await set.LongCountAsync(ct);

        var totalUsers           = await TotalAsync(db.Users);
        var totalSpaces          = await TotalAsync(db.Spaces);
        var totalChannels        = await TotalAsync(db.Channels);
        var totalMessages        = await TotalAsync(db.Messages);
        var totalCustomItems     = await db.Items.Where(i => i.IsReference).LongCountAsync(ct);
        var totalCouponsRedeemed = await db.CouponRedemption.LongCountAsync(ct);
        var totalApps            = await db.AppEntities.LongCountAsync(ct);
        var totalBots            = await db.BotEntities.LongCountAsync(ct);

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

    public async Task<DatabaseDiagnostics> PingDatabaseAsync(CancellationToken ct = default)
    {
        try
        {
            await using var db = await dbFactory.CreateDbContextAsync(ct);

            var stopwatch = Stopwatch.StartNew();

            await db.Users.AsNoTracking().AnyAsync(ct);

            stopwatch.Stop();

            var provider = db.Database.ProviderName ?? "Unknown";

            return new DatabaseDiagnostics(provider, 10, 0, 1, true, stopwatch.ElapsedMilliseconds, null);
        }
        catch (Exception ex)
        {
            return new DatabaseDiagnostics("CockroachDB/PostgreSQL", 0, 0, 0, false, null, ex.Message);
        }
    }

    public async Task<EmailJournalPage> ReadEmailJournalAsync(Guid? userId, int offset, int limit, CancellationToken ct = default)
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

    // ── items and coupons ────────────────────────────────────────────────────────────────────

    public async Task<ItemTemplateList> GetItemTemplatesAsync(CancellationToken ct = default)
    {
        await using var db = await dbFactory.CreateDbContextAsync(ct);

        var templates = await db.Items
           .AsNoTracking()
           .Where(i => i.IsReference)
           .Include(i => i.Scenario)
           .ToListAsync(ct);

        var boxedItems = await AdminBoxContents.ReadAsync(db, templates.Select(t => t.Scenario), ct);

        var result = templates.Select(t => new ItemTemplateInfo(
            t.Id,
            t.TemplateId,
            t.IsUsable,
            t.IsGiftable,
            t.IsAffectBadge,
            t.TTL.HasValue ? (int)t.TTL.Value.TotalSeconds : null,
            t.CreatedAt.UtcDateTime,
            t.Scenario switch
            {
                RedeemScenario       => ItemScenarioKind.RedeemCode,
                PremiumScenario      => ItemScenarioKind.Premium,
                QualifierBox         => ItemScenarioKind.QualifierBox,
                MultipleQualifierBox => ItemScenarioKind.QualifierBox,
                BoxScenario          => ItemScenarioKind.Box,
                _                    => ItemScenarioKind.None
            },
            AdminBoxContents.Of(t.Scenario, boxedItems)
        )).ToList();

        return new ItemTemplateList(new IonArray<ItemTemplateInfo>(result));
    }

    public async Task<bool> ReferenceItemExistsAsync(Guid itemId, CancellationToken ct = default)
    {
        await using var db = await dbFactory.CreateDbContextAsync(ct);

        return await db.Items.AnyAsync(i => i.IsReference && i.Id == itemId, ct);
    }

    public async Task<DeleteItemResult> DeleteItemFromUserInventoryAsync(Guid userId, Guid itemId, CancellationToken ct = default)
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

    public async Task<DeleteItemResult> DeleteItemTemplateAsync(Guid itemId, CancellationToken ct = default)
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

            var isUsedInBox = await db.Items.AnyAsync(i => i.IsReference
                && ((i.Scenario is QualifierBox && ((QualifierBox)i.Scenario).ReferenceItemId == itemId)
                 || (i.Scenario is MultipleQualifierBox && ((MultipleQualifierBox)i.Scenario).ReferenceItemIds.Contains(itemId))), ct);

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

    public async Task<CreateItemTemplateResult> CreateItemTemplateAsync(CreateItemTemplateInput input, CancellationToken ct = default)
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

                // Only boxes that hold the first requested item can be a duplicate; the exact comparison below
                // runs on those alone.
                var firstId = boxContentIds[0];
                var holdingFirst = boxContentIds.Count == 1
                    ? db.Items.Where(i => i.IsReference && i.Scenario is QualifierBox
                                       && ((QualifierBox)i.Scenario).ReferenceItemId == firstId)
                    : db.Items.Where(i => i.IsReference && i.Scenario is MultipleQualifierBox
                                       && ((MultipleQualifierBox)i.Scenario).ReferenceItemIds.Contains(firstId));

                var existingBoxTemplates = await holdingFirst
                   .AsNoTracking()
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
                            var newSet      = boxContentIds.OrderBy(x => x).ToList();
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
                    Key     = ArgonId.New(),
                    Edition = input.templateId
                },
                ItemScenarioKind.QualifierBox when boxContentIds?.Count == 1 => new QualifierBox
                {
                    Key             = ArgonId.New(),
                    ReferenceItemId = boxContentIds[0]
                },
                ItemScenarioKind.QualifierBox when boxContentIds?.Count > 1 => new MultipleQualifierBox
                {
                    Key              = ArgonId.New(),
                    ReferenceItemIds = boxContentIds
                },
                ItemScenarioKind.Premium => new PremiumScenario
                {
                    Key    = ArgonId.New(),
                    PlanId = ""
                },
                ItemScenarioKind.RedeemCode => new RedeemScenario
                {
                    Key        = ArgonId.New(),
                    Code       = "",
                    ServiceKey = ""
                },
                _ => null
            };

            var item = new ArgonItemEntity
            {
                Id            = ArgonId.New(),
                TemplateId    = input.templateId,
                IsUsable      = input.isUsable,
                IsGiftable    = input.isGiftable,
                IsAffectBadge = input.isAffectBadge,
                IsReference   = true,
                OwnerId       = UserEntity.SystemUser,
                TTL           = input.ttl.HasValue ? TimeSpan.FromSeconds(input.ttl.Value) : null,
                Scenario      = scenario,
                ScenarioKey   = scenario?.Key
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

    public async Task<CouponList> GetCouponsAsync(CancellationToken ct = default)
    {
        await using var db = await dbFactory.CreateDbContextAsync(ct);

        var couponEntities = await db.Coupons
           .AsNoTracking()
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

    public async Task<CreateCouponResult> CreateCouponAsync(CreateCouponInput input, CancellationToken ct = default)
    {
        try
        {
            await using var db = await dbFactory.CreateDbContextAsync(ct);

            var exists = await db.Coupons.AnyAsync(c => c.Code == input.code, ct);
            if (exists)
                return new CreateCouponResult(false, null, "Coupon with this code already exists");

            var coupon = new ArgonCouponEntity
            {
                Id                    = ArgonId.New(),
                Code                  = input.code,
                Description           = input.description,
                ValidFrom             = input.validFrom,
                ValidTo               = input.validTo,
                MaxRedemptions        = input.maxRedemptions,
                RedemptionCount       = 0,
                IsActive              = true,
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

    // ── tenant directory ─────────────────────────────────────────────────────────────────────

    public async Task<TenantDirectoryList> GetTenantDirectoryAsync(CancellationToken ct = default)
    {
        await using var db = await dbFactory.CreateDbContextAsync(ct);

        var entities = await db.TenantDirectory
           .AsNoTracking()
           .Where(t => !t.IsDeleted)
           .OrderBy(t => t.Domain)
           .ToListAsync(ct);

        var tenants = entities.Select(t => new TenantInfo(
            t.Id, t.Domain, t.InstanceUrl, t.IsVerified, t.OrgName, t.OwnerUserId, t.Notes, t.CreatedAt.UtcDateTime)).ToList();

        return new TenantDirectoryList(new IonArray<TenantInfo>(tenants));
    }

    public async Task<TenantActionResult> CreateTenantAsync(string domain, string instanceUrl, string? orgName, Guid? ownerUserId,
        string? notes, CancellationToken ct = default)
    {
        await using var db = await dbFactory.CreateDbContextAsync(ct);

        if (await db.TenantDirectory.AnyAsync(t => t.Domain == domain && !t.IsDeleted, ct))
            return new TenantActionResult(false, null, "A tenant for this domain already exists");

        var entity = new TenantDirectoryEntity
        {
            Domain      = domain,
            InstanceUrl = instanceUrl,
            IsVerified  = false, // verify is a separate, system-operator-gated step
            OrgName     = orgName,
            OwnerUserId = ownerUserId,
            Notes       = notes
        };
        db.TenantDirectory.Add(entity);
        await db.SaveChangesAsync(ct);

        return new TenantActionResult(true, entity.Id, null);
    }

    public async Task<AdminTenantChange> UpdateTenantAsync(Guid tenantId, string instanceUrl, string? orgName, string? notes,
        CancellationToken ct = default)
    {
        await using var db = await dbFactory.CreateDbContextAsync(ct);

        var t = await db.TenantDirectory.FirstOrDefaultAsync(x => x.Id == tenantId && !x.IsDeleted, ct);
        if (t is null)
            return new AdminTenantChange(new TenantActionResult(false, tenantId, "Tenant not found"), null);

        t.InstanceUrl = instanceUrl;
        t.OrgName     = orgName;
        t.Notes       = notes;
        await db.SaveChangesAsync(ct);

        return new AdminTenantChange(new TenantActionResult(true, t.Id, null), t.Domain);
    }

    public async Task<AdminTenantChange> SetTenantVerifiedAsync(Guid callerOperatorId, Guid tenantId, bool isVerified,
        CancellationToken ct = default)
    {
        await using var db = await dbFactory.CreateDbContextAsync(ct);

        if (!await db.Operators.AnyAsync(o => o.Id == callerOperatorId && !o.IsDeleted && o.IsSystemOperator, ct))
            return new AdminTenantChange(new TenantActionResult(false, tenantId, "Only system operators can verify tenants"), null);

        var t = await db.TenantDirectory.FirstOrDefaultAsync(x => x.Id == tenantId && !x.IsDeleted, ct);
        if (t is null)
            return new AdminTenantChange(new TenantActionResult(false, tenantId, "Tenant not found"), null);

        t.IsVerified = isVerified;
        await db.SaveChangesAsync(ct);

        return new AdminTenantChange(new TenantActionResult(true, tenantId, null), t.Domain);
    }

    public async Task<AdminTenantChange> DeleteTenantAsync(Guid tenantId, CancellationToken ct = default)
    {
        await using var db = await dbFactory.CreateDbContextAsync(ct);

        var t = await db.TenantDirectory.FirstOrDefaultAsync(x => x.Id == tenantId && !x.IsDeleted, ct);
        if (t is null)
            return new AdminTenantChange(new TenantActionResult(false, tenantId, "Tenant not found"), null);

        t.IsDeleted = true;
        t.DeletedAt = DateTimeOffset.UtcNow;
        await db.SaveChangesAsync(ct);

        return new AdminTenantChange(new TenantActionResult(true, tenantId, null), t.Domain);
    }
}
