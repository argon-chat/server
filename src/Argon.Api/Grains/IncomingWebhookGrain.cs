namespace Argon.Grains;

using Argon.Features.EF;
using Microsoft.Extensions.Caching.Hybrid;

/// <summary>
/// One incoming webhook, keyed by its id. Counts this webhook's posts for the per-minute limit. The
/// row is read on every call, so a new token, a rename or a delete takes effect with nothing sent here.
/// </summary>
public class IncomingWebhookGrain(
    IDbContextFactory<ApplicationDbContext> context,
    IOptions<MessagesOptions> messageOptions,
    HybridCache cache,
    ILogger<IncomingWebhookGrain> logger) : Grain, IIncomingWebhookGrain
{
    public const int PerMinute = 30;

    private static readonly TimeSpan Window        = TimeSpan.FromMinutes(1);
    private static readonly TimeSpan LastUsedEvery = TimeSpan.FromMinutes(1);

    // The entry and lifetimes the Ion interceptor uses, so dropping it on a lockdown reaches posts too.
    private static readonly HybridCacheEntryOptions LockdownCacheOptions = new()
    {
        Expiration           = TimeSpan.FromSeconds(30),
        LocalCacheExpiration = TimeSpan.FromSeconds(10)
    };

    private sealed record Hook(Guid ChannelId, string Name, string? AvatarFileId, string TokenHash, Guid CreatorId);

    private DateTimeOffset                   lastUsedWritten;
    private readonly Queue<DateTimeOffset> accepted = new();

    public async Task<WebhookExecution> ExecuteAsync(string token, string? content, string? username)
    {
        var row = await ReadAsync();

        if (row is null)
        {
            // Nothing to keep this activation for.
            DeactivateOnIdle();
            return new WebhookExecution(WebhookExecutionOutcome.NotFound);
        }

        // The same answer for an unknown webhook and a wrong token.
        if (string.IsNullOrEmpty(token) || !ChannelWebhookEntity.TokenMatches(token, row.TokenHash))
            return new WebhookExecution(WebhookExecutionOutcome.NotFound);

        var now = DateTimeOffset.UtcNow;
        while (accepted.Count > 0 && now - accepted.Peek() >= Window)
            accepted.Dequeue();

        if (accepted.Count >= PerMinute)
        {
            var wait = Window - (now - accepted.Peek());
            return new WebhookExecution(WebhookExecutionOutcome.RateLimited, Math.Max(1, (int)Math.Ceiling(wait.TotalSeconds)));
        }

        accepted.Enqueue(now);

        var text = content?.Trim();
        if (string.IsNullOrEmpty(text) || text.Length > messageOptions.Value.MaxTextLength)
            return new WebhookExecution(WebhookExecutionOutcome.Invalid);

        var name = row.Name;
        if (!string.IsNullOrWhiteSpace(username))
        {
            name = ChannelWebhookEntity.CleanName(username);
            if (name.Length is 0 or > ChannelWebhookEntity.MaxNameLength || ChannelWebhookEntity.IsReservedName(name))
                return new WebhookExecution(WebhookExecutionOutcome.Invalid);
        }

        if (await CreatorLockedDownAsync(row.CreatorId))
            return new WebhookExecution(WebhookExecutionOutcome.Forbidden);

        WebhookPostOutcome outcome;
        try
        {
            outcome = await GrainFactory.GetGrain<IChannelWebhooksGrain>(row.ChannelId)
               .PostWebhookMessage(new MessageWebhookAuthor(this.GetPrimaryKey(), name, row.AvatarFileId), text);
        }
        catch (KeyNotFoundException)
        {
            // The channel is gone and its activation cannot start.
            outcome = WebhookPostOutcome.ChannelGone;
        }

        switch (outcome)
        {
            case WebhookPostOutcome.Posted:
                await TouchAsync(now);
                return new WebhookExecution(WebhookExecutionOutcome.Accepted);
            case WebhookPostOutcome.Invalid:
                return new WebhookExecution(WebhookExecutionOutcome.Invalid);
            case WebhookPostOutcome.ChannelBusy:
                return new WebhookExecution(WebhookExecutionOutcome.RateLimited, 1);
            default:
                return new WebhookExecution(WebhookExecutionOutcome.NotFound);
        }
    }

    private async Task<Hook?> ReadAsync()
    {
        var id = this.GetPrimaryKey();

        await using var ctx = await context.CreateDbContextAsync();
        return await ctx.ChannelWebhooks.AsNoTracking()
           .Where(w => w.Id == id)
           .Select(w => new Hook(w.ChannelId, w.Name, w.AvatarFileId, w.TokenHash, w.CreatorId))
           .FirstOrDefaultAsync();
    }

    private async Task<bool> CreatorLockedDownAsync(Guid creatorId)
    {
        // Resolved here: the cache may run the factory off this activation's scheduler.
        var directory = GrainFactory.GetGrain<IIdentityDirectoryGrain>(Guid.Empty);

        var snapshot = await cache.GetOrCreateAsync(
            ArgonRequestContext.LockdownCacheKey(creatorId),
            async ct => await directory.GetLockdownAsync(creatorId, ct),
            LockdownCacheOptions);

        if (snapshot.ExpiresAt is { } expiry && expiry <= DateTimeOffset.UtcNow)
            return false;

        return ReportActionPlanner.SeverityOf(snapshot.Reason) >= LockdownSeverity.Critical;
    }

    private async Task TouchAsync(DateTimeOffset now)
    {
        if (now - lastUsedWritten < LastUsedEvery)
            return;

        lastUsedWritten = now;

        try
        {
            var id = this.GetPrimaryKey();

            await using var ctx = await context.CreateDbContextAsync();
            await ctx.ChannelWebhooks
               .Where(w => w.Id == id)
               .ExecuteUpdateAsync(s => s.SetProperty(w => w.LastUsedAt, now));
        }
        catch (Exception e)
        {
            logger.LogWarning(e, "failed to note the use of webhook {WebhookId}", this.GetPrimaryKey());
        }
    }
}
