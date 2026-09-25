namespace Argon.Grains;

using Argon.Features.EF;
using Api.Entities.Data;
using Api.Features.Utils;
using Argon.Api.Grains.Interfaces;
using Argon.Core.Features.Logic;
using Orleans.Concurrency;
using System.Linq;
using Core.Entities.Data;

[StatelessWorker]
public class InventoryGrain(
    IDbContextFactory<ApplicationDbContext> context,
    ILogger<IInventoryGrain> logger,
    ISystemNotificationService systemNotification) : Grain, IInventoryGrain
{
    public async Task<List<DetailedInventoryItem>> GetReferencesItemsAsync(CancellationToken ct = default)
    {
        var items = await context.Select(ctx => ctx.Items
           .AsNoTracking()
           .Include(x => x.Scenario)
           .Where(x => x.IsReference)
           .ToListAsync(ct), ct);

        return items.Select(x => new DetailedInventoryItem(x.ToDto(), UnwrapScenarioForCase(x.Scenario, items))).ToList();
    }

    private List<InventoryItem> UnwrapScenarioForCase(ItemUseScenario? scenario, List<ArgonItemEntity> items)
    {
        if (scenario is QualifierBox qualifierBox)
        {
            var containedItem = items.FirstOrDefault(x => x.Id == qualifierBox.ReferenceItemId);
            return containedItem is not null ? [containedItem.ToDto()] : [];
        }

        if (scenario is MultipleQualifierBox multipleQualifierBox)
        {
            return multipleQualifierBox.ReferenceItemIds
               .Select(refId => items.FirstOrDefault(x => x.Id == refId))
               .Where(x => x is not null)
               .Select(x => x!.ToDto())
               .ToList();
        }

        return [];
    }

    public async Task<bool> GiveItemFor(Guid userId, Guid refItemId, CancellationToken ct = default)
    {
        await using var ctx           = await context.CreateDbContextAsync(ct);
        var             referenceItem = await ctx.Items.FirstOrDefaultAsync(x => x.Id == refItemId && x.IsReference, ct);

        if (referenceItem is null)
            return false;

        var item = referenceItem with
        {
            IsReference = false,
            Id = ArgonId.New(),
            OwnerId = userId,
            ReceivedFrom = null,
            CreatedAt = DateTimeOffset.UtcNow
        };

        ctx.Set<ArgonItemEntity>().Add(item);

        await ctx.SaveChangesAsync(ct);

        await EnsureUnreadAsync(ctx, userId, item.Id, item.TemplateId, ct);
        await systemNotification.CreateAsync(userId, SystemNotificationType.ItemReceived, item.Id, $"New item: {item.TemplateId}", null, ct: ct);

        if (!item.IsAffectBadge)
            return true;

        await AddBadgeToProfileAsync(ctx, userId, item.TemplateId, ct);
        await ctx.SaveChangesAsync(ct);

        return true;
    }

    public async Task<Guid?> CreateReferenceItem(string templateId, bool isUsable, bool isGiftable, bool isAffectToBadge,
        CancellationToken ct = default)
    {
        await using var ctx = await context.CreateDbContextAsync(ct);
        var item = new ArgonItemEntity()
        {
            IsReference   = true,
            Id            = ArgonId.New(),
            OwnerId       = UserEntity.SystemUser,
            ReceivedFrom  = null,
            CreatedAt     = DateTimeOffset.UtcNow,
            TemplateId    = templateId,
            IsUsable      = isUsable,
            IsAffectBadge = isAffectToBadge,
            IsGiftable    = isGiftable
        };
        ctx.Set<ArgonItemEntity>().Add(item);

        await ctx.SaveChangesAsync(ct);
        return item.Id;
    }

    public async Task<Guid?> CreateCaseForReferenceItem(Guid refItemId,
        string caseTemplateId, CancellationToken ct = default)
    {
        await using var ctx = await context.CreateDbContextAsync(ct);
        var referenceItem = await ctx.Items
           .FirstOrDefaultAsync(x => x.Id == refItemId && x.IsReference, ct);

        if (referenceItem is null)
            return null;


        var @case = new ArgonItemEntity()
        {
            IsReference   = true,
            Id            = ArgonId.New(),
            OwnerId       = UserEntity.SystemUser,
            ReceivedFrom  = null,
            CreatedAt     = DateTimeOffset.UtcNow,
            TemplateId    = caseTemplateId,
            IsUsable      = true,
            IsAffectBadge = false,
            IsGiftable    = false,
            UseVector     = ItemUseVector.QualifierBox,
            Scenario = new QualifierBox
            {
                Key             = ArgonId.New(),
                ReferenceItemId = referenceItem.Id
            }
        };

        ctx.Set<ArgonItemEntity>().Add(@case);

        await ctx.SaveChangesAsync(ct);

        return @case.Id;
    }

    public async Task<bool> GiveCoinFor(Guid userId, string coinTemplateId, CancellationToken ct = default)
    {
        await using var ctx = await context.CreateDbContextAsync(ct);

        var coin = new ArgonItemEntity
        {
            Id            = ArgonId.New(),
            OwnerId       = userId,
            TemplateId    = coinTemplateId,
            IsReference   = false,
            IsUsable      = false,
            IsGiftable    = false,
            IsAffectBadge = true,
            ReceivedFrom  = null,
            CreatedAt     = DateTimeOffset.UtcNow
        };

        ctx.Set<ArgonItemEntity>().Add(coin);
        await ctx.SaveChangesAsync(ct);

        await EnsureUnreadAsync(ctx, userId, coin.Id, coinTemplateId, ct);
        await systemNotification.CreateAsync(userId, SystemNotificationType.ItemReceived, coin.Id, $"New coin: {coinTemplateId}", null, ct: ct);

        if (coin.IsAffectBadge)
        {
            await AddBadgeToProfileAsync(ctx, userId, coin.TemplateId, ct);
            await ctx.SaveChangesAsync(ct);
        }

        logger.LogInformation("Gave coin {TemplateId} to user {UserId}", coinTemplateId, userId);
        return true;
    }

    public async Task<List<InventoryItem>> GetItemsForUserAsync(Guid userId, CancellationToken ct = default)
        => await context.Select(ctx => ctx.Items
           .AsNoTracking()
           .Where(x => x.OwnerId == userId)
           .Where(x => x.TTL == null || x.CreatedAt + x.TTL > DateTimeOffset.UtcNow)
           .ToListAsync(ct)
           .Then(x => x.Select(q => q.ToDto()).ToList()), ct);


    public async Task<List<InventoryItem>> GetMyItemsAsync(CancellationToken ct = default)
        => await GetItemsForUserAsync(this.GetUserId(), ct);

    public async Task<List<InventoryNotification>> GetNotificationsAsync(CancellationToken ct = default)
        => await context.Select(async ctx =>
        {
            var items = await ctx.UnreadInventoryItems
               .AsNoTracking()
               .Where(u => u.OwnerUserId == this.GetUserId())
               .OrderByDescending(u => u.CreatedAt)
               .Select(u => new
                {
                    u.InventoryItemId,
                    u.TemplateId,
                    u.CreatedAt
                })
               .ToListAsync(ct);

            return items
               .Select(u => new InventoryNotification(
                    u.InventoryItemId,
                    u.TemplateId,
                    u.CreatedAt.UtcDateTime
                ))
               .ToList();
        }, ct);

    [OneWay]
    public async Task MarkSeenAsync(List<Guid> inventoryItemIds, CancellationToken ct = default)
    {
        if (inventoryItemIds.Count == 0) return;

        var userId = this.GetUserId();

        await using var ctx = await context.CreateDbContextAsync(ct);

        var deleted = await ctx.UnreadInventoryItems
           .Where(x => x.OwnerUserId == userId && inventoryItemIds.Contains(x.InventoryItemId))
           .ExecuteDeleteAsync(ct);

        if (deleted > 0)
        {
            await systemNotification.MarkAllReadAsync(userId, SystemNotificationType.ItemReceived, ct);
        }
    }

    public async Task<bool> UseItemAsync(Guid itemId, CancellationToken ct = default)
    {
        await using var ctx    = await context.CreateDbContextAsync(ct);
        var             userId = this.GetUserId();

        try
        {
            var used = await ctx.Database.CreateExecutionStrategy().ExecuteAsync<UsedItem?>(async token =>
            {
                ctx.ChangeTracker.Clear();
                await using var trx = await ctx.Database.BeginTransactionAsync(token);

                var usableItem = await ctx.Set<ArgonItemEntity>()
                   .Include(i => i.Scenario)
                   .FirstOrDefaultAsync(i => i.Id == itemId && i.OwnerId == userId, cancellationToken: token);

                if (usableItem is null) return null;
                if (!usableItem.IsUsable) return null;
                if (usableItem.Scenario is null) return null;

                List<ArgonItemEntity> grantedItems;

                switch (usableItem.Scenario)
                {
                    case PremiumScenario:
                    {
                        ctx.Remove(usableItem);
                        grantedItems = [];
                        break;
                    }
                    case QualifierBox qualifierBox:
                    {
                        var granted = await UseQualifierBox(ctx, qualifierBox, userId, usableItem, token);
                        if (granted is null)
                            return null;

                        grantedItems = [granted];
                        break;
                    }
                    case MultipleQualifierBox multipleQualifierBox:
                    {
                        grantedItems = await UseMultipleQualifierBox(ctx, multipleQualifierBox, userId, usableItem, token);
                        if (grantedItems.Count == 0)
                            return null;

                        break;
                    }
                    default:
                        return null;
                }

                await ctx.SaveChangesAsync(token);
                await trx.CommitAsync(token);

                return new UsedItem(usableItem.Id, usableItem.Scenario as PremiumScenario,
                    grantedItems.Select(x => (x.Id, x.TemplateId)).ToList());
            }, ct);

            if (used is null)
                return false;

            if (used.Premium is { } premium)
            {
                var tier = premium.PlanId switch
                {
                    "ultima_annual" => Argon.Entities.UltimaTier.Annual,
                    _               => Argon.Entities.UltimaTier.Monthly
                };

                await GrainFactory.GetGrain<IUltimaGrain>(userId)
                   .ActivateSubscriptionAsync(tier, premium.DurationDays, null, used.ItemId, ct);

                return true;
            }

            // Each granted item is announced under its own template, not the box's.
            foreach (var (grantedId, templateId) in used.Granted)
            {
                await EnsureUnreadAsync(ctx, userId, grantedId, templateId, ct);
            }

            foreach (var (grantedId, templateId) in used.Granted)
            {
                await systemNotification.CreateAsync(userId, SystemNotificationType.ItemReceived, grantedId, $"New item: {templateId}", null, ct: ct);
            }

            return true;
        }
        catch (Exception e)
        {
            logger.LogCritical(e, "failed use item");
            return false;
        }
    }

    private sealed record UsedItem(Guid ItemId, PremiumScenario? Premium, List<(Guid Id, string TemplateId)> Granted);

    private async Task<ArgonItemEntity?> UseQualifierBox(ApplicationDbContext ctx, QualifierBox box, Guid userId, ArgonItemEntity boxItem,
        CancellationToken ct = default)
    {
        var proto = box.ReferenceItem ?? await ctx.Set<ArgonItemEntity>()
           .AsNoTracking()
           .FirstOrDefaultAsync(i => i.Id == box.ReferenceItemId, ct);

        if (proto is null)
            return null;

        ctx.Remove(boxItem);

        var granted = proto with
        {
            Id = ArgonId.New(),
            OwnerId = userId,
            IsReference = false,
            CreatedAt = DateTimeOffset.UtcNow,
            ReceivedFrom = null
        };

        await ctx.AddAsync(granted, ct);

        if (granted.IsAffectBadge)
        {
            await AddBadgeToProfileAsync(ctx, userId, granted.TemplateId, ct);
        }

        return granted;
    }

    private async Task<List<ArgonItemEntity>> UseMultipleQualifierBox(ApplicationDbContext ctx, MultipleQualifierBox box, Guid userId, ArgonItemEntity boxItem,
        CancellationToken ct = default)
    {
        var referenceItemIds = box.ReferenceItemIds.ToList();
        if (referenceItemIds.Count == 0)
            return [];

        var protos = await ctx.Set<ArgonItemEntity>()
           .AsNoTracking()
           .Where(i => referenceItemIds.Contains(i.Id))
           .ToListAsync(ct);

        if (protos.Count == 0)
            return [];

        ctx.Remove(boxItem);

        var grantedItems = new List<ArgonItemEntity>();

        foreach (var proto in protos)
        {
            var granted = proto with
            {
                Id = ArgonId.New(),
                OwnerId = userId,
                IsReference = false,
                CreatedAt = DateTimeOffset.UtcNow,
                ReceivedFrom = null
            };

            await ctx.AddAsync(granted, ct);
            grantedItems.Add(granted);

            if (granted.IsAffectBadge)
            {
                await AddBadgeToProfileAsync(ctx, userId, granted.TemplateId, ct);
            }
        }

        return grantedItems;
    }

    public async Task<RedeemError?> RedeemCodeAsync(string code, CancellationToken ct = default)
    {
        var userId = this.GetUserId();

        await using var ctx = await context.CreateDbContextAsync(ct);

        var (error, item) = await ctx.Database.CreateExecutionStrategy().ExecuteAsync(async token =>
        {
            ctx.ChangeTracker.Clear();

            await using var tx = await ctx.Database.BeginTransactionAsync(token);

            var coupon = await ctx.Coupons
               .AsNoTracking()
               .FirstOrDefaultAsync(c => c.Code == code, token);

            if (coupon == null)
                return (RedeemError.NOT_FOUND, (ArgonItemEntity?)null);

            if (!coupon.IsActive)
                return (RedeemError.INACTIVE, null);

            var now = DateTime.UtcNow;
            if (now < coupon.ValidFrom || now > coupon.ValidTo)
                return (RedeemError.EXPIRED, null);

            if (await ctx.CouponRedemption.AnyAsync(r => r.CouponId == coupon.Id && r.UserId == userId, token))
                return (RedeemError.ALREADY, null);

            // Claimed in the WHERE, so concurrent redemptions cannot take the coupon past its limit.
            var claimed = await ctx.Coupons
               .Where(c => c.Id == coupon.Id && c.RedemptionCount < c.MaxRedemptions)
               .ExecuteUpdateAsync(u => u.SetProperty(c => c.RedemptionCount, c => c.RedemptionCount + 1), token);

            if (claimed == 0)
                return (RedeemError.LIMIT_REACHED, null);

            var redemption = new ArgonCouponRedemptionEntity
            {
                Id         = ArgonId.New(),
                CouponId   = coupon.Id,
                UserId     = userId,
                RedeemedAt = now
            };

            ctx.CouponRedemption.Add(redemption);

            ArgonItemEntity? granted = null;

            if (coupon.ReferenceItemEntityId.HasValue)
            {
                var referenceItem = await ctx.Items.AsNoTracking().FirstAsync(x => x.Id == coupon.ReferenceItemEntityId, token);

                granted = referenceItem with
                {
                    IsReference  = false,
                    Id           = ArgonId.New(),
                    OwnerId      = userId,
                    RedemptionId = redemption.Id,
                    ReceivedFrom = null,
                    CreatedAt    = DateTimeOffset.UtcNow
                };

                ctx.Set<ArgonItemEntity>().Add(granted);
            }

            await ctx.SaveChangesAsync(token);
            await tx.CommitAsync(token);

            return ((RedeemError?)null, granted);
        }, ct);

        if (error is not null || item is null)
            return error;

        await EnsureUnreadAsync(ctx, userId, item.Id, item.TemplateId, ct);
        await systemNotification.CreateAsync(userId, SystemNotificationType.ItemReceived, item.Id, $"New item: {item.TemplateId}", null, ct: ct);

        if (item.IsAffectBadge)
        {
            await AddBadgeToProfileAsync(ctx, userId, item.TemplateId, ct);
            await ctx.SaveChangesAsync(ct);
        }

        return null;
    }

    private static async Task EnsureUnreadAsync(ApplicationDbContext ctx, Guid ownerId, Guid inventoryItemId, string templateId, CancellationToken ct)
    {
        if (await ctx.UnreadInventoryItems.AnyAsync(u => u.OwnerUserId == ownerId && u.InventoryItemId == inventoryItemId, ct))
            return;

        var unread = ctx.UnreadInventoryItems.Add(new ArgonItemNotificationEntity
        {
            OwnerUserId     = ownerId,
            InventoryItemId = inventoryItemId,
            TemplateId      = templateId,
            CreatedAt       = DateTimeOffset.UtcNow
        });

        try
        {
            await ctx.SaveChangesAsync(ct);
        }
        catch (DbUpdateException e) when (e.IsUniqueViolation())
        {
            unread.State = EntityState.Detached;
        }
    }

    private async Task AddBadgeToProfileAsync(ApplicationDbContext ctx, Guid userId, string templateId, CancellationToken ct)
    {
        var profile = await ctx.UserProfiles
           .FirstOrDefaultAsync(p => p.UserId == userId, ct);

        if (profile is null)
        {
            logger.LogWarning("Profile not found for user {UserId}, cannot add badge {TemplateId}", userId, templateId);
            return;
        }

        if (!profile.Badges.Contains(templateId))
        {
            profile.Badges.Add(templateId);
            ctx.UserProfiles.Update(profile);
            logger.LogInformation("Added badge {TemplateId} to user {UserId} profile", templateId, userId);
        }
    }

    public async Task<bool> GiveUltimaGiftAsync(Guid recipientId, string planId, int durationDays, Guid senderId, string? giftMessage, CancellationToken ct = default)
    {
        await using var ctx = await context.CreateDbContextAsync(ct);

        var scenario = new PremiumScenario
        {
            Key          = ArgonId.New(),
            PlanId       = planId,
            DurationDays = durationDays,
            GiftMessage  = giftMessage
        };

        var item = new ArgonItemEntity
        {
            Id            = ArgonId.New(),
            OwnerId       = recipientId,
            TemplateId    = planId == "ultima_annual" ? "gift_ultima_annual" : "gift_ultima_monthly",
            IsReference   = false,
            IsUsable      = true,
            IsGiftable    = false,
            IsAffectBadge = false,
            UseVector     = ItemUseVector.Premium,
            ReceivedFrom  = senderId,
            Scenario      = scenario,
            ScenarioKey   = scenario.Key,
            CreatedAt     = DateTimeOffset.UtcNow
        };

        ctx.Set<ArgonItemEntity>().Add(item);
        await ctx.SaveChangesAsync(ct);

        await EnsureUnreadAsync(ctx, recipientId, item.Id, item.TemplateId, ct);

        var sender = await ctx.Users.AsNoTracking()
           .Where(x => x.Id == senderId)
           .Select(x => new { x.DisplayName })
           .FirstOrDefaultAsync(ct);

        var senderName = sender?.DisplayName ?? "Someone";

        await systemNotification.CreateAsync(recipientId, SystemNotificationType.ItemReceived, item.Id,
            $"🎁 {senderName} sent you Argon Ultima!", giftMessage, ct: ct);

        logger.LogInformation("Gave Ultima gift from {SenderId} to {RecipientId}, plan {PlanId}", senderId, recipientId, planId);
        return true;
    }

    public async Task GiveBoostItemsAsync(Guid userId, int count, int durationDays, CancellationToken ct = default)
    {
        await using var ctx = await context.CreateDbContextAsync(ct);

        var now = DateTimeOffset.UtcNow;
        var ttl = TimeSpan.FromDays(durationDays);
        var itemIds = new List<Guid>(count);

        for (var i = 0; i < count; i++)
        {
            var id = ArgonId.New();
            itemIds.Add(id);

            var item = new ArgonItemEntity
            {
                Id            = id,
                OwnerId       = userId,
                TemplateId    = "item_boost",
                IsReference   = false,
                IsUsable      = false,
                IsGiftable    = false,
                IsAffectBadge = false,
                TTL           = ttl,
                ReceivedFrom  = null,
                CreatedAt     = now
            };

            ctx.Set<ArgonItemEntity>().Add(item);
        }

        await ctx.SaveChangesAsync(ct);

        // Mark as unread + notify (batch after save)
        foreach (var itemId in itemIds)
        {
            await EnsureUnreadAsync(ctx, userId, itemId, "item_boost", ct);
            await systemNotification.CreateAsync(userId, SystemNotificationType.ItemReceived, itemId, "New boost item", null, ct: ct);
        }

        logger.LogInformation("Gave {Count} boost items to user {UserId} (TTL: {Days}d)", count, userId, durationDays);
    }
}