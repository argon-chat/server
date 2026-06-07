namespace Argon.Grains;

using Argon.Core.Entities.Data;
using Argon.Core.Features.CoreLogic.Privacy;
using Argon.Entities;
using Grains.Interfaces;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging;

/// <summary>
/// Per-user privacy policy evaluator (GuidKey = owner userId). Loads the owner's privacy
/// rules into a small cache (refreshed every few minutes) and evaluates the "about-me"
/// disposition for a given viewer + key, honoring per-space overrides and allow/deny
/// exceptions. "Contacts" mode resolves against the friendship table.
/// </summary>
public sealed class PrivacyPolicyGrain(
    IDbContextFactory<ApplicationDbContext> contextFactory,
    ILogger<PrivacyPolicyGrain> logger) : Grain, IPrivacyPolicyGrain
{
    private List<PrivacyRuleEntity> _rules = new();
    private DateTime _cacheExpiry = DateTime.MinValue;
    private static readonly TimeSpan CacheDuration = TimeSpan.FromMinutes(5);

    private Guid OwnerId => this.GetPrimaryKey();

    private async ValueTask EnsureCacheLoadedAsync()
    {
        if (DateTime.UtcNow < _cacheExpiry) return;
        await using var ctx = await contextFactory.CreateDbContextAsync();
        _rules = await ctx.Set<PrivacyRuleEntity>()
           .AsNoTracking()
           .Where(r => r.UserId == OwnerId)
           .ToListAsync();
        _cacheExpiry = DateTime.UtcNow + CacheDuration;
    }

    /// <summary>Space-scoped rule wins over the global one for the same key.</summary>
    private PrivacyRuleEntity? ResolveRule(string key, Guid? spaceId)
    {
        PrivacyRuleEntity? global = null;
        foreach (var r in _rules)
        {
            if (r.Key != key) continue;
            if (spaceId.HasValue && r.ScopeSpaceId == spaceId.Value) return r; // exact space match
            if (r.ScopeSpaceId is null) global = r;
        }
        return global;
    }

    public async ValueTask<bool> EvaluateAsync(Guid viewerId, string key, Guid? spaceId)
    {
        // The owner can always act on their own subject.
        if (viewerId == OwnerId) return true;

        await EnsureCacheLoadedAsync();
        var rule = ResolveRule(key, spaceId);

        var mode = rule?.Mode ?? PrivacyKeys.DefaultModeFor(key);

        // Deny beats allow beats mode default.
        if (rule is not null)
        {
            if (rule.DenyExceptions.Contains(viewerId)) return false;
            if (rule.AllowExceptions.Contains(viewerId)) return true;
        }

        return mode switch
        {
            PrivacyMode.Everybody => true,
            PrivacyMode.Nobody    => false,
            PrivacyMode.Contacts  => await AreContactsAsync(viewerId),
            _                     => true,
        };
    }

    private async ValueTask<bool> AreContactsAsync(Guid viewerId)
    {
        try
        {
            await using var ctx = await contextFactory.CreateDbContextAsync();
            return await ctx.Set<FriendshipEntity>().AnyAsync(f =>
                (f.UserId == OwnerId && f.FriendId == viewerId) ||
                (f.UserId == viewerId && f.FriendId == OwnerId));
        }
        catch (Exception e)
        {
            logger.LogError(e, "privacy: friendship lookup failed for owner {Owner}", OwnerId);
            return false; // fail closed for Contacts mode
        }
    }

    public async ValueTask<PrivacyRuleDto?> GetRuleAsync(string key, Guid? spaceId)
    {
        await EnsureCacheLoadedAsync();
        var rule = ResolveRule(key, spaceId);
        if (rule is null) return null;
        return new PrivacyRuleDto(rule.Key, rule.Mode, rule.ScopeSpaceId,
            new List<Guid>(rule.AllowExceptions), new List<Guid>(rule.DenyExceptions));
    }

    public async ValueTask SetRuleAsync(PrivacyRuleInput input)
    {
        if (!PrivacyKeys.All.Contains(input.Key))
            throw new InvalidOperationException($"Unknown privacy key: {input.Key}");

        await using var ctx = await contextFactory.CreateDbContextAsync();

        var existing = await ctx.Set<PrivacyRuleEntity>().FirstOrDefaultAsync(r =>
            r.UserId == OwnerId && r.Key == input.Key && r.ScopeSpaceId == input.ScopeSpaceId);

        if (existing is null)
        {
            ctx.Add(new PrivacyRuleEntity
            {
                Id              = Guid.NewGuid(),
                UserId          = OwnerId,
                Key             = input.Key,
                Mode            = input.Mode,
                ScopeSpaceId    = input.ScopeSpaceId,
                AllowExceptions = input.AllowExceptions,
                DenyExceptions  = input.DenyExceptions,
                UpdatedAt       = DateTimeOffset.UtcNow,
            });
        }
        else
        {
            existing.Mode            = input.Mode;
            existing.AllowExceptions = input.AllowExceptions;
            existing.DenyExceptions  = input.DenyExceptions;
            existing.UpdatedAt       = DateTimeOffset.UtcNow;
            ctx.Update(existing);
        }

        await ctx.SaveChangesAsync();
        await InvalidateCacheAsync();
    }

    public ValueTask InvalidateCacheAsync()
    {
        _cacheExpiry = DateTime.MinValue;
        return ValueTask.CompletedTask;
    }
}
