namespace Argon.Grains;

using Argon.Features.EF;

/// <summary>
/// One incoming webhook, keyed by its id. Holds the row (token hash included) between calls and
/// counts this webhook's posts for the per-minute limit.
/// </summary>
public class IncomingWebhookGrain(
    IDbContextFactory<ApplicationDbContext> context,
    IOptions<MessagesOptions> messageOptions,
    ILogger<IncomingWebhookGrain> logger) : Grain, IIncomingWebhookGrain
{
    public const int PerMinute = 30;

    private static readonly TimeSpan Window          = TimeSpan.FromMinutes(1);
    private static readonly TimeSpan LastUsedEvery   = TimeSpan.FromMinutes(1);

    private sealed record Hook(Guid ChannelId, string Name, string? AvatarFileId, string TokenHash);

    private Hook?                            hook;
    private bool                             loaded;
    private DateTimeOffset                   lastUsedWritten;
    private readonly Queue<DateTimeOffset> accepted = new();

    public async Task<WebhookExecution> ExecuteAsync(string token, string? content, string? username)
    {
        var row = await LoadAsync();

        // The same answer for an unknown webhook and a wrong token.
        if (row is null || string.IsNullOrEmpty(token) || !ChannelWebhookEntity.TokenMatches(token, row.TokenHash))
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
        if (username?.Trim() is { Length: > 0 } custom)
        {
            if (custom.Length > ChannelWebhookEntity.MaxNameLength)
                return new WebhookExecution(WebhookExecutionOutcome.Invalid);
            name = custom;
        }

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

    public Task ForgetAsync()
    {
        hook   = null;
        loaded = false;
        return Task.CompletedTask;
    }

    private async Task<Hook?> LoadAsync()
    {
        if (loaded)
            return hook;

        var id = this.GetPrimaryKey();

        await using var ctx = await context.CreateDbContextAsync();
        hook = await ctx.ChannelWebhooks.AsNoTracking()
           .Where(w => w.Id == id)
           .Select(w => new Hook(w.ChannelId, w.Name, w.AvatarFileId, w.TokenHash))
           .FirstOrDefaultAsync();

        loaded = true;
        return hook;
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
