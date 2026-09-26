namespace Argon.Grains.Interfaces;

using Orleans.Concurrency;

/// <summary>
/// The webhook half of <see cref="IChannelGrain"/>: the same activation, keyed by the channel id.
/// Management calls need ManageChannels in the channel.
/// </summary>
[Alias("Argon.Grains.Interfaces.IChannelWebhooksGrain")]
public interface IChannelWebhooksGrain : IGrainWithGuidKey
{
    [Alias(nameof(CreateWebhook))]
    Task<IssuedWebhookResult> CreateWebhook(string name, CancellationToken ct = default);

    [Alias(nameof(GetWebhooks))]
    Task<List<ChannelWebhook>> GetWebhooks(CancellationToken ct = default);

    [Alias(nameof(RenameWebhook))]
    Task<IUpdateWebhookResult> RenameWebhook(Guid webhookId, string name, CancellationToken ct = default);

    [Alias(nameof(RegenerateWebhookToken))]
    Task<IssuedWebhookResult> RegenerateWebhookToken(Guid webhookId, CancellationToken ct = default);

    [Alias(nameof(DeleteWebhook))]
    Task<bool> DeleteWebhook(Guid webhookId, CancellationToken ct = default);

    /// <summary>
    /// Posts a webhook message. The token has already been checked, so no user permission applies;
    /// the channel's send cap and the text limit do.
    /// </summary>
    [Alias(nameof(PostWebhookMessage))]
    Task<WebhookPostOutcome> PostWebhookMessage(MessageWebhookAuthor author, string text);
}

/// <summary>A webhook with its token, which leaves the server only here; or why there is none.</summary>
[GenerateSerializer, Immutable]
public sealed record IssuedWebhookResult(
    [property: Id(0)] ChannelWebhook? Webhook,
    [property: Id(1)] string? Token,
    [property: Id(2)] ChannelWebhookError Error);

[GenerateSerializer]
public enum WebhookPostOutcome
{
    Posted,
    Invalid,
    ChannelBusy,
    ChannelGone
}

/// <summary>
/// An incoming webhook, keyed by its id: checks the token, rate-limits and posts. Every post is checked
/// against the stored row, so a new token, a rename or a delete needs no call here.
/// </summary>
[Alias("Argon.Grains.Interfaces.IIncomingWebhookGrain")]
public interface IIncomingWebhookGrain : IGrainWithGuidKey
{
    [Alias(nameof(ExecuteAsync))]
    Task<WebhookExecution> ExecuteAsync(string token, string? content, string? username);
}

[GenerateSerializer]
public enum WebhookExecutionOutcome
{
    Accepted,
    // Unknown webhook and wrong token alike.
    NotFound,
    Invalid,
    RateLimited,
    // The creator is under a critical lockdown.
    Forbidden
}

[GenerateSerializer, Immutable]
public sealed record WebhookExecution(
    [property: Id(0)] WebhookExecutionOutcome Outcome,
    [property: Id(1)] int RetryAfterSeconds = 0);

/// <summary>The announcement read counter, on the channel's activation.</summary>
[Alias("Argon.Grains.Interfaces.IChannelInsightsGrain")]
public interface IChannelInsightsGrain : IGrainWithGuidKey
{
    [Alias(nameof(GetReadCount))]
    Task<IReadCountResult> GetReadCount(long messageId, CancellationToken ct = default);

    /// <summary>Counts for up to 50 posts at once; the ones the caller may not see are left out.</summary>
    [Alias(nameof(GetReadCounts))]
    Task<List<ReadCountEntry>> GetReadCounts(List<long> messageIds, CancellationToken ct = default);
}
