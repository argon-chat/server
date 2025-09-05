namespace Argon.Grains;

using Api.Entities.Data;
using Api.Features.Utils;
using Argon.Api.Grains.Interfaces;
using Orleans.Concurrency;

[StatelessWorker]
public class InventoryGrain(IDbContextFactory<ApplicationDbContext> context) : Grain, IInventoryGrain
{
    public async Task<List<InventoryItem>> GetMyItemsAsync(CancellationToken ct = default)
        => await context.Select(ctx => ctx.Items
           .AsNoTracking()
           .Where(x => x.OwnerId == this.GetUserId())
           .ToListAsync(ct)
           .Then(x => x.Select(q => q.ToDto()).ToList()), ct);

    public async Task<List<InventoryNotification>> GetNotificationsAsync(CancellationToken ct = default)
        => await context.Select(ctx => (
                from u in ctx.UnreadInventoryItems.AsNoTracking().Where(x => x.OwnerUserId == this.GetUserId())
                join t in ctx.Items.AsNoTracking() on u.TemplateId equals t.TemplateId
                orderby u.CreatedAt descending
                select new InventoryNotification(u.InventoryItemId, u.TemplateId, u.CreatedAt.UtcDateTime))
           .ToListAsync(ct), ct);

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
        INSERT INTO unread_inventory_items (""OwnerUserId"", ""InventoryItemId"", ""TemplateId"", ""CreatedAt"")
        VALUES ({ownerId}, {inventoryItemId}, {templateId}, NOW())
        ON CONFLICT (""OwnerUserId"", ""InventoryItemId"") DO NOTHING;", ct);
}