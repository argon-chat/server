namespace Argon.Grains;

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
    INotificationCounterService notificationCounter) : Grain, IInventoryGrain
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
            Id = Guid.NewGuid(),
            OwnerId = userId,
            ReceivedFrom = null,
            CreatedAt = DateTimeOffset.Now
        };

        ctx.Set<ArgonItemEntity>().Add(item);

        await ctx.SaveChangesAsync(ct);

        await EnsureUnreadAsync(ctx, userId, item.Id, item.TemplateId, ct);
        await notificationCounter.IncrementAsync(userId, NotificationCounterType.UnreadInventoryItems, 1, ct);

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
            Id            = Guid.NewGuid(),
            OwnerId       = UserEntity.SystemUser,
            ReceivedFrom  = null,
            CreatedAt     = DateTimeOffset.Now,
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
            Id            = Guid.NewGuid(),
            OwnerId       = UserEntity.SystemUser,
            ReceivedFrom  = null,
            CreatedAt     = DateTimeOffset.Now,
            TemplateId    = caseTemplateId,
            IsUsable      = true,
            IsAffectBadge = false,
            IsGiftable    = false,
            UseVector     = ItemUseVector.QualifierBox,
            Scenario = new QualifierBox
            {
                Key             = Guid.NewGuid(),
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
            Id            = Guid.NewGuid(),
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
        await notificationCounter.IncrementAsync(userId, NotificationCounterType.UnreadInventoryItems, 1, ct);

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
            await notificationCounter.DecrementAsync(userId, NotificationCounterType.UnreadInventoryItems, deleted, ct);
        }
    }

    [OneWay]
    public async Task MarkAllSeenAsync(Guid ownerUserId, CancellationToken ct = default)
    {
        await using var ctx = await context.CreateDbContextAsync(ct);

        var deleted = await ctx.UnreadInventoryItems
           .Where(x => x.OwnerUserId == ownerUserId)
           .ExecuteDeleteAsync(ct);

        if (deleted > 0)
        {
            await notificationCounter.ResetAsync(ownerUserId, NotificationCounterType.UnreadInventoryItems, ct);
        }
    }

    public async Task<bool> UseItemAsync(Guid itemId, CancellationToken ct = default)
    {
        await using var ctx    = await context.CreateDbContextAsync(ct);
        var             userId = this.GetUserId();

        var strategy = ctx.Database.CreateExecutionStrategy();

        return await strategy.ExecuteAsync(async () =>
        {
            await using var trx = await ctx.Database.BeginTransactionAsync(ct);

            try
            {
                var usableItem = await ctx.Set<ArgonItemEntity>()
                   .Include(i => i.Scenario)
                   .FirstOrDefaultAsync(i => i.Id == itemId && i.OwnerId == userId, cancellationToken: ct);

                if (usableItem is null) return false;
                if (!usableItem.IsUsable) return false;
                if (usableItem.Scenario is null) return false;

                List<Guid> grantedItemIds;

                switch (usableItem.Scenario)
                {
                    case QualifierBox qualifierBox:
                    {
                        var itemId = await UseQualifierBox(ctx, qualifierBox, userId, usableItem, ct);
                        if (itemId is null)
                        {
                            await trx.RollbackAsync(ct);
                            return false;
                        }

                        grantedItemIds = [itemId.Value];
                        break;
                    }
                    case MultipleQualifierBox multipleQualifierBox:
                    {
                        grantedItemIds = await UseMultipleQualifierBox(ctx, multipleQualifierBox, userId, usableItem, ct);
                        if (grantedItemIds.Count == 0)
                        {
                            await trx.RollbackAsync(ct);
                            return false;
                        }

                        break;
                    }
                    default:
                        await trx.RollbackAsync(ct);
                        return false;
                }

                await ctx.SaveChangesAsync(ct);
                await trx.CommitAsync(ct);

                foreach (var grantedId in grantedItemIds)
                {
                    await EnsureUnreadAsync(ctx, userId, grantedId, usableItem.TemplateId, ct);
                }

                await notificationCounter.IncrementAsync(userId, NotificationCounterType.UnreadInventoryItems, grantedItemIds.Count, ct);

                return true;
            }
            catch (Exception e)
            {
                logger.LogCritical(e, "failed use item");
                await trx.RollbackAsync(ct);
                return false;
            }
        });
    }

    private async Task<Guid?> UseQualifierBox(ApplicationDbContext ctx, QualifierBox box, Guid userId, ArgonItemEntity boxItem,
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
            Id = Guid.NewGuid(),
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

        return granted.Id;
    }

    private async Task<List<Guid>> UseMultipleQualifierBox(ApplicationDbContext ctx, MultipleQualifierBox box, Guid userId, ArgonItemEntity boxItem,
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

        var grantedIds = new List<Guid>();

        foreach (var proto in protos)
        {
            var granted = proto with
            {
                Id = Guid.NewGuid(),
                OwnerId = userId,
                IsReference = false,
                CreatedAt = DateTimeOffset.UtcNow,
                ReceivedFrom = null
            };

            await ctx.AddAsync(granted, ct);
            grantedIds.Add(granted.Id);

            if (granted.IsAffectBadge)
            {
                await AddBadgeToProfileAsync(ctx, userId, granted.TemplateId, ct);
            }
        }

        return grantedIds;
    }

    public async Task<RedeemError?> RedeemCodeAsync(string code, CancellationToken ct = default)
    {
        await using var ctx = await context.CreateDbContextAsync(ct);
        var coupon = await ctx.Coupons
           .Include(c => c.Redemptions)
           .Include(c => c.ReferenceItemEntity)
           .FirstOrDefaultAsync(c => c.Code == code, ct);

        if (coupon == null)
            return RedeemError.NOT_FOUND;

        if (!coupon.IsActive)
            return RedeemError.INACTIVE;

        var now = DateTime.UtcNow;
        if (now < coupon.ValidFrom || now > coupon.ValidTo)
            return RedeemError.EXPIRED;

        if (coupon.RedemptionCount >= coupon.MaxRedemptions)
            return RedeemError.LIMIT_REACHED;

        var userId = this.GetUserId();

        if (coupon.Redemptions.Any(r => r.UserId == userId))
            return RedeemError.ALREADY;

        var redemption = new ArgonCouponRedemptionEntity
        {
            Id         = Guid.NewGuid(),
            CouponId   = coupon.Id,
            Coupon     = coupon,
            UserId     = userId,
            RedeemedAt = now
        };

        await ctx.CouponRedemption.AddAsync(redemption, ct);

        if (coupon.ReferenceItemEntityId.HasValue)
        {
            var referenceItem = await ctx.Items.FirstAsync(x => x.Id == coupon.ReferenceItemEntityId, ct);

            var item = referenceItem with
            {
                IsReference = false,
                Id = Guid.NewGuid(),
                OwnerId = userId,
                RedemptionId = redemption.Id,
                ReceivedFrom = null,
                CreatedAt = DateTimeOffset.Now
            };

            ctx.Set<ArgonItemEntity>().Add(item);
            redemption.Items.Add(item);
            coupon.RedemptionCount++;

            await ctx.SaveChangesAsync(ct);

            await EnsureUnreadAsync(ctx, userId, item.Id, item.TemplateId, ct);
            await notificationCounter.IncrementAsync(userId, NotificationCounterType.UnreadInventoryItems, 1, ct);

            if (!item.IsAffectBadge) return null;
            await AddBadgeToProfileAsync(ctx, userId, item.TemplateId, ct);
        }
        else
            coupon.RedemptionCount++;

        await ctx.SaveChangesAsync(ct);

        return null;
    }

    private async Task EnsureUnreadAsync(ApplicationDbContext ctx, Guid ownerId, Guid inventoryItemId, string templateId, CancellationToken ct)
        => await ctx.Database.ExecuteSqlInterpolatedAsync($@"
        INSERT INTO ""UnreadInventoryItems"" (""OwnerUserId"", ""InventoryItemId"", ""TemplateId"", ""CreatedAt"")
        VALUES ({ownerId}, {inventoryItemId}, {templateId}, {DateTimeOffset.UtcNow})
        ON CONFLICT (""OwnerUserId"", ""InventoryItemId"") DO NOTHING;", ct);

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
}