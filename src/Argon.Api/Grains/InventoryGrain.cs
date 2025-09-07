namespace Argon.Grains;

using Api.Entities.Data;
using Api.Features.Utils;
using Argon.Api.Grains.Interfaces;
using Orleans.Concurrency;

[StatelessWorker]
public class InventoryGrain(IDbContextFactory<ApplicationDbContext> context, ILogger<IInventoryGrain> logger) : Grain, IInventoryGrain
{
    public async Task<List<InventoryItem>> GetMyItemsAsync(CancellationToken ct = default)
        => await context.Select(ctx => ctx.Items
           .AsNoTracking()
           .Where(x => x.OwnerId == this.GetUserId())
           .ToListAsync(ct)
           .Then(x => x.Select(q => q.ToDto()).ToList()), ct);

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
        await using var ctx = await context.CreateDbContextAsync(ct);
        await ctx.UnreadInventoryItems
           .Where(x => x.OwnerUserId == this.GetUserId() && inventoryItemIds.Contains(x.InventoryItemId))
           .ExecuteDeleteAsync(ct);
    }

    [OneWay]
    public async Task MarkAllSeenAsync(Guid ownerUserId, CancellationToken ct = default)
    {
        await using var ctx = await context.CreateDbContextAsync(ct);
        await ctx.UnreadInventoryItems
           .Where(x => x.OwnerUserId == ownerUserId)
           .ExecuteDeleteAsync(ct);
    }

    public async Task<bool> UseItemAsync(Guid itemId, CancellationToken ct = default)
    {
        await using var ctx    = await context.CreateDbContextAsync(ct);
        var             userId = this.GetUserId();

        await using var trx = await ctx.Database.BeginTransactionAsync(ct);

        try
        {
            var usableItem = await ctx.Set<ArgonItemEntity>()
               .Include(i => i.Scenario)
               .FirstOrDefaultAsync(i => i.Id == itemId && i.OwnerId == userId, cancellationToken: ct);

            if (usableItem is null) return false;
            if (!usableItem.IsUsable) return false;
            if (usableItem.Scenario is null) return false;

            var newItemId = usableItem.Scenario switch
            {
                QualifierBox qualifierBox => await UseQualifierBox(ctx, qualifierBox, userId, usableItem, ct),
                _                         => null
            };

            if (newItemId is null)
            {
                await trx.RollbackAsync(ct);
                return false;
            }

            await ctx.SaveChangesAsync(ct);
            await trx.CommitAsync(ct);

            await EnsureUnreadAsync(ctx, userId, newItemId.Value, usableItem.TemplateId, ct);
            return true;
        }
        catch (Exception e)
        {
            logger.LogCritical(e, "failed use item");
            await trx.RollbackAsync(ct);
            return false;
        }
    }

    private async Task<Guid?> UseQualifierBox(ApplicationDbContext ctx, QualifierBox box, Guid userId, ArgonItemEntity boxItem, CancellationToken ct = default)
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
            ConcurrencyToken = 0,
            CreatedAt = DateTimeOffset.UtcNow,
            ReceivedFrom = null
        };

        await ctx.AddAsync(granted, ct);
        return granted.Id;
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
                ConcurrencyToken = 0,
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
        }
        else
        {
            coupon.RedemptionCount++;
            await ctx.SaveChangesAsync(ct);
        }

        return null;
    }

    private async Task EnsureUnreadAsync(ApplicationDbContext ctx, Guid ownerId, Guid inventoryItemId, string templateId, CancellationToken ct)
        => await ctx.Database.ExecuteSqlInterpolatedAsync($@"
        INSERT INTO ""UnreadInventoryItems"" (""OwnerUserId"", ""InventoryItemId"", ""TemplateId"", ""CreatedAt"")
        VALUES ({ownerId}, {inventoryItemId}, {templateId}, {DateTimeOffset.UtcNow.ToUnixTimeMilliseconds()})
        ON CONFLICT (""OwnerUserId"", ""InventoryItemId"") DO NOTHING;", ct);
}