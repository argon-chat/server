namespace Argon.Grains;

using Argon.Core.Features.Integrations.Xsolla;
using Argon.Core.Features.Logic;
using Argon.Core.Features.Transport;
using Argon.Entities;
using Grains.Interfaces;
using ion.runtime;

public class UltimaGrain(
    IDbContextFactory<ApplicationDbContext> context,
    AppHubServer appHubServer,
    IUserSessionDiscoveryService sessionDiscovery,
    IUserSessionNotifier notifier,
    IXsollaService xsolla,
    ILogger<IUltimaGrain> logger) : Grain, IUltimaGrain
{
    private static readonly TimeSpan TransferCooldown = TimeSpan.FromDays(7);
    private static readonly TimeSpan GracePeriod      = TimeSpan.FromDays(3);

    private Guid UserId => this.GetPrimaryKey();

    public async Task<UltimaSubscriptionInfo?> GetSubscriptionAsync(CancellationToken ct = default)
    {
        await using var ctx = await context.CreateDbContextAsync(ct);

        var sub = await ctx.UltimaSubscriptions
           .AsNoTracking()
           .Where(x => x.UserId == UserId && x.Status != UltimaStatus.Expired)
           .OrderByDescending(x => x.CreatedAt)
           .FirstOrDefaultAsync(ct);

        if (sub is null)
            return null;

        var usedSlots = await ctx.SpaceBoosts
           .AsNoTracking()
           .CountAsync(x => x.SubscriptionId == sub.Id && x.SpaceId != null, ct);

        return new UltimaSubscriptionInfo(
            sub.Id,
            MapTier(sub.Tier),
            MapStatus(sub.Status),
            sub.StartsAt.UtcDateTime,
            sub.ExpiresAt.UtcDateTime,
            sub.AutoRenew,
            sub.BoostSlots,
            usedSlots,
            null);
    }

    public async Task<string?> GetXsollaSubscriptionIdAsync(CancellationToken ct = default)
    {
        await using var ctx = await context.CreateDbContextAsync(ct);

        var sub = await ctx.UltimaSubscriptions
           .AsNoTracking()
           .Where(x => x.UserId == UserId && x.Status != UltimaStatus.Expired)
           .OrderByDescending(x => x.CreatedAt)
           .Select(x => x.XsollaSubscriptionId)
           .FirstOrDefaultAsync(ct);

        return sub;
    }

    public async Task<List<UltimaTransaction>> GetTransactionHistoryAsync(CancellationToken ct = default)
    {
        await using var ctx = await context.CreateDbContextAsync(ct);

        var txns = await ctx.PaymentTransactions
           .AsNoTracking()
           .Where(x => x.UserId == UserId)
           .OrderByDescending(x => x.CreatedAt)
           .Take(50)
           .ToListAsync(ct);

        return txns.Select(t => new UltimaTransaction(
            t.XsollaTxId,
            t.CreatedAt.UtcDateTime,
            t.Amount,
            t.Currency,
            t.PlanExternalId,
            t.BoostPackType,
            t.BoostCount,
            t.RecipientId,
            t.TransactionType,
            t.CardSuffix,
            t.CardBrand,
            t.Status
        )).ToList();
    }

    public async Task<List<UltimaBoost>> GetBoostsAsync(CancellationToken ct = default)
    {
        await using var ctx = await context.CreateDbContextAsync(ct);

        var now = DateTimeOffset.UtcNow;
        var boosts = await ctx.SpaceBoosts
           .AsNoTracking()
           .Include(x => x.Space)
           .Where(x => x.UserId == UserId)
           .Where(x => x.ExpiresAt == null || x.ExpiresAt > now)
           .ToListAsync(ct);

        return boosts.Select(b => new UltimaBoost(
            b.Id,
            b.SpaceId,
            b.Space?.Name,
            b.AppliedAt?.UtcDateTime,
            b.TransferCooldownUntil?.UtcDateTime,
            b.Source
        )).ToList();
    }

    public async Task<IApplyBoostResult> ApplyBoostAsync(Guid boostId, Guid spaceId, CancellationToken ct = default)
    {
        await using var ctx = await context.CreateDbContextAsync(ct);

        var boost = await ctx.SpaceBoosts
           .FirstOrDefaultAsync(x => x.Id == boostId && x.UserId == UserId, ct);

        if (boost is null)
            return new FailedApplyBoost(ApplyBoostError.NOT_FOUND);

        if (boost.SpaceId is not null)
            return new FailedApplyBoost(ApplyBoostError.ALREADY_APPLIED);

        var isMember = await ctx.UsersToServerRelations
           .AnyAsync(x => x.UserId == UserId && x.SpaceId == spaceId, ct);

        if (!isMember)
            return new FailedApplyBoost(ApplyBoostError.NOT_A_MEMBER);

        boost.SpaceId   = spaceId;
        boost.AppliedAt = DateTimeOffset.UtcNow;

        await ctx.SaveChangesAsync(ct);

        await GrainFactory.GetGrain<ISpaceBoostGrain>(spaceId).AddBoostAsync(UserId, boostId, ct);

        return new SuccessApplyBoost();
    }

    public async Task<ITransferBoostResult> TransferBoostAsync(Guid boostId, Guid newSpaceId, CancellationToken ct = default)
    {
        await using var ctx = await context.CreateDbContextAsync(ct);

        var boost = await ctx.SpaceBoosts
           .FirstOrDefaultAsync(x => x.Id == boostId && x.UserId == UserId, ct);

        if (boost is null)
            return new FailedTransfer(TransferBoostError.NOT_FOUND);

        if (boost.SpaceId is null)
            return new FailedTransfer(TransferBoostError.NOT_APPLIED);

        if (boost.TransferCooldownUntil.HasValue && boost.TransferCooldownUntil > DateTimeOffset.UtcNow)
            return new FailedTransfer(TransferBoostError.ON_COOLDOWN);

        var isMember = await ctx.UsersToServerRelations
           .AnyAsync(x => x.UserId == UserId && x.SpaceId == newSpaceId, ct);

        if (!isMember)
            return new FailedTransfer(TransferBoostError.NOT_A_MEMBER);

        var oldSpaceId = boost.SpaceId.Value;

        boost.SpaceId              = newSpaceId;
        boost.AppliedAt            = DateTimeOffset.UtcNow;
        boost.TransferCooldownUntil = DateTimeOffset.UtcNow + TransferCooldown;

        await ctx.SaveChangesAsync(ct);

        await GrainFactory.GetGrain<ISpaceBoostGrain>(oldSpaceId).RemoveBoostAsync(boostId, ct);
        await GrainFactory.GetGrain<ISpaceBoostGrain>(newSpaceId).AddBoostAsync(UserId, boostId, ct);

        return new SuccessTransfer();
    }

    public async Task<bool> RemoveBoostAsync(Guid boostId, CancellationToken ct = default)
    {
        await using var ctx = await context.CreateDbContextAsync(ct);

        var boost = await ctx.SpaceBoosts
           .FirstOrDefaultAsync(x => x.Id == boostId && x.UserId == UserId, ct);

        if (boost is null)
            return false;

        var spaceId = boost.SpaceId;

        boost.SpaceId   = null;
        boost.AppliedAt = null;

        await ctx.SaveChangesAsync(ct);

        if (spaceId.HasValue)
            await GrainFactory.GetGrain<ISpaceBoostGrain>(spaceId.Value).RemoveBoostAsync(boostId, ct);

        return true;
    }

    public async Task ActivateSubscriptionAsync(UltimaTier tier, int durationDays, string? xsollaSubId, Guid? fromItemId, CancellationToken ct = default)
    {
        await using var ctx = await context.CreateDbContextAsync(ct);

        var existing = await ctx.UltimaSubscriptions
           .FirstOrDefaultAsync(x => x.UserId == UserId && (x.Status == UltimaStatus.Active || x.Status == UltimaStatus.GracePeriod), ct);

        var now = DateTimeOffset.UtcNow;

        if (existing is not null)
        {
            // Extend existing subscription
            if (existing.ExpiresAt > now)
                existing.ExpiresAt = existing.ExpiresAt.AddDays(durationDays);
            else
                existing.ExpiresAt = now.AddDays(durationDays);

            existing.Status = UltimaStatus.Active;
            existing.Tier   = tier;
            if (xsollaSubId is not null)
                existing.XsollaSubscriptionId = xsollaSubId;

            await ctx.SaveChangesAsync(ct);
        }
        else
        {
            var sub = new UltimaSubscriptionEntity
            {
                Id                   = Guid.NewGuid(),
                UserId               = UserId,
                Tier                 = tier,
                Status               = UltimaStatus.Active,
                StartsAt             = now,
                ExpiresAt            = now.AddDays(durationDays),
                AutoRenew            = xsollaSubId is not null,
                BoostSlots           = 2,
                XsollaSubscriptionId = xsollaSubId,
                ActivatedFromItemId  = fromItemId,
                CreatedAt            = now
            };

            ctx.UltimaSubscriptions.Add(sub);
            await ctx.SaveChangesAsync(ct);

            // Create subscription boost slots
            for (var i = 0; i < sub.BoostSlots; i++)
            {
                ctx.SpaceBoosts.Add(new SpaceBoostEntity
                {
                    Id             = Guid.NewGuid(),
                    UserId         = UserId,
                    SpaceId        = null,
                    SubscriptionId = sub.Id,
                    Source         = BoostSource.Subscription,
                    CreatedAt      = now
                });
            }

            await ctx.SaveChangesAsync(ct);
        }

        // Set PREMIUM flag
        await ctx.Users
           .Where(x => x.Id == UserId)
           .ExecuteUpdateAsync(s => s.SetProperty(x => x.HasActiveUltima, true), ct);

        logger.LogInformation("Activated Ultima {Tier} for user {UserId}, duration {Days} days", tier, UserId, durationDays);

        // Sync subscriber attribute to Xsolla for promotion targeting (numeric 1 = active)
        // Polly will retry transient errors; if all retries fail, log and continue — DB is already committed
        try
        {
            await xsolla.UpdateUserAttributeAsync(UserId, "ultima_subscriber", "1");
        }
        catch (Exception ex)
        {
            logger.LogError(ex, "Failed to sync Xsolla subscriber attribute for user {UserId} after retries", UserId);
        }
    }

    public async Task ExpireSubscriptionAsync(CancellationToken ct = default)
    {
        await using var ctx = await context.CreateDbContextAsync(ct);

        var sub = await ctx.UltimaSubscriptions
           .FirstOrDefaultAsync(x => x.UserId == UserId && (x.Status == UltimaStatus.Active || x.Status == UltimaStatus.GracePeriod), ct);

        if (sub is null)
            return;

        sub.Status = UltimaStatus.Expired;

        // Remove subscription-linked boosts that are applied to spaces
        var subscriptionBoosts = await ctx.SpaceBoosts
           .Where(x => x.SubscriptionId == sub.Id)
           .ToListAsync(ct);

        var affectedSpaceIds = subscriptionBoosts
           .Where(x => x.SpaceId.HasValue)
           .Select(x => x.SpaceId!.Value)
           .Distinct()
           .ToList();

        ctx.SpaceBoosts.RemoveRange(subscriptionBoosts);

        // Clear PREMIUM flag
        await ctx.Users
           .Where(x => x.Id == UserId)
           .ExecuteUpdateAsync(s => s.SetProperty(x => x.HasActiveUltima, false), ct);

        // Reset premium profile customizations
        await ctx.UserProfiles
           .Where(x => x.UserId == UserId)
           .ExecuteUpdateAsync(s => s
               .SetProperty(x => x.BackgroundId, (int?)null)
               .SetProperty(x => x.VoiceCardEffectId, (int?)null)
               .SetProperty(x => x.AvatarFrameId, (int?)null)
               .SetProperty(x => x.NickEffectId, (int?)null)
               .SetProperty(x => x.PrimaryColor, (int?)null)
               .SetProperty(x => x.AccentColor, (int?)null)
               .SetProperty(x => x.CustomStatus, (string?)null)
               .SetProperty(x => x.CustomStatusIconId, (string?)null), ct);

        await ctx.SaveChangesAsync(ct);

        // Recalculate affected spaces
        foreach (var spaceId in affectedSpaceIds)
            await GrainFactory.GetGrain<ISpaceBoostGrain>(spaceId).RecalculateAsync(ct);

        // Broadcast profile reset to all user's spaces
        await GrainFactory.GetGrain<IUserGrain>(UserId).ResetPremiumProfileAsync(ct);

        logger.LogInformation("Expired Ultima subscription for user {UserId}, removed {Count} boosts from {Spaces} spaces",
            UserId, subscriptionBoosts.Count, affectedSpaceIds.Count);

        // Clear subscriber attribute in Xsolla (numeric 0 = inactive)
        // Polly will retry transient errors; if all retries fail, log and continue — DB is already committed
        try
        {
            await xsolla.UpdateUserAttributeAsync(UserId, "ultima_subscriber", "0");
        }
        catch (Exception ex)
        {
            logger.LogError(ex, "Failed to clear Xsolla subscriber attribute for user {UserId} after retries", UserId);
        }
    }

    public async Task<bool> CancelSubscriptionAsync(CancellationToken ct = default)
    {
        await using var ctx = await context.CreateDbContextAsync(ct);

        var sub = await ctx.UltimaSubscriptions
           .FirstOrDefaultAsync(x => x.UserId == UserId && x.Status == UltimaStatus.Active, ct);

        if (sub is null)
            return false;

        sub.Status      = UltimaStatus.Cancelled;
        sub.AutoRenew   = false;
        sub.CancelledAt = DateTimeOffset.UtcNow;

        await ctx.SaveChangesAsync(ct);

        logger.LogInformation("Cancelled Ultima subscription for user {UserId}, expires at {ExpiresAt}", UserId, sub.ExpiresAt);
        return true;
    }

    public async Task GrantPurchasedBoostsAsync(int count, BoostSource source, string? xsollaTxId, int? durationDays = null, CancellationToken ct = default)
    {
        await using var ctx = await context.CreateDbContextAsync(ct);

        var now = DateTimeOffset.UtcNow;
        var expiresAt = durationDays.HasValue ? now.AddDays(durationDays.Value) : (DateTimeOffset?)null;

        for (var i = 0; i < count; i++)
        {
            ctx.SpaceBoosts.Add(new SpaceBoostEntity
            {
                Id                  = Guid.NewGuid(),
                UserId              = UserId,
                SpaceId             = null,
                SubscriptionId      = null,
                Source              = source,
                XsollaTransactionId = xsollaTxId,
                ExpiresAt           = expiresAt,
                CreatedAt           = now
            });
        }

        await ctx.SaveChangesAsync(ct);

        logger.LogInformation("Granted {Count} purchased boosts to user {UserId} (expires: {ExpiresAt})", count, UserId, expiresAt);
    }

    public async Task SaveTransactionAsync(string txId, string transactionType, string? planExternalId, string? boostPackType,
        int? boostCount, Guid? recipientId, string? amount, string? currency,
        string? cardSuffix = null, string? cardBrand = null, long? paymentAccountId = null, CancellationToken ct = default)
    {
        await using var ctx = await context.CreateDbContextAsync(ct);

        var exists = await ctx.PaymentTransactions.AnyAsync(x => x.XsollaTxId == txId, ct);
        if (exists) return;

        ctx.PaymentTransactions.Add(new PaymentTransactionEntity
        {
            Id               = Guid.NewGuid(),
            UserId           = UserId,
            XsollaTxId       = txId,
            TransactionType  = transactionType,
            PlanExternalId   = planExternalId,
            BoostPackType    = boostPackType,
            BoostCount       = boostCount,
            Amount           = amount,
            Currency         = currency,
            RecipientId      = recipientId,
            CardSuffix       = cardSuffix,
            CardBrand        = cardBrand,
            PaymentAccountId = paymentAccountId,
            Status           = "done",
            CreatedAt        = DateTimeOffset.UtcNow,
        });

        await ctx.SaveChangesAsync(ct);
    }

    public async Task MarkTransactionRefundedAsync(string txId, CancellationToken ct = default)
    {
        await using var ctx = await context.CreateDbContextAsync(ct);
        await ctx.PaymentTransactions
           .Where(x => x.XsollaTxId == txId)
           .ExecuteUpdateAsync(s => s.SetProperty(x => x.Status, "refunded"), ct);
    }

    private static UltimaPlan MapTier(UltimaTier tier) => tier switch
    {
        UltimaTier.Monthly => UltimaPlan.Monthly,
        UltimaTier.Annual  => UltimaPlan.Annual,
        _                  => UltimaPlan.Monthly
    };

    private static UltimaSubscriptionStatus MapStatus(UltimaStatus status) => status switch
    {
        UltimaStatus.Active      => UltimaSubscriptionStatus.Active,
        UltimaStatus.Cancelled   => UltimaSubscriptionStatus.Cancelled,
        UltimaStatus.Expired     => UltimaSubscriptionStatus.Expired,
        UltimaStatus.GracePeriod => UltimaSubscriptionStatus.GracePeriod,
        _                        => UltimaSubscriptionStatus.Active
    };
}
