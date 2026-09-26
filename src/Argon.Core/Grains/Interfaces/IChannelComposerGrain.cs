namespace Argon.Grains.Interfaces;

using Orleans.Concurrency;

/// <summary>
/// A channel's scheduled posts, keyed by the channel id. Publishing runs on a reminder that is armed
/// for the earliest pending post, so it survives deactivation and silo restarts.
/// </summary>
[Alias("Argon.Grains.Interfaces.IChannelComposerGrain")]
public interface IChannelComposerGrain : IGrainWithGuidKey
{
    [Alias(nameof(SchedulePostAsync))]
    Task<ISchedulePostResult> SchedulePostAsync(Guid spaceId, string text, List<IMessageEntity> entities, DateTimeOffset publishAt);

    [Alias(nameof(ReschedulePostAsync))]
    Task<ISchedulePostResult> ReschedulePostAsync(Guid spaceId, Guid postId, DateTimeOffset publishAt);

    [Alias(nameof(CancelScheduledPostAsync))]
    Task<bool> CancelScheduledPostAsync(Guid spaceId, Guid postId);

    [Alias(nameof(GetScheduledPostsAsync))]
    Task<List<ScheduledPost>> GetScheduledPostsAsync(Guid spaceId);

    /// <summary>Cancels the author's pending posts here, after a Critical lockdown or before an erasure.</summary>
    [Alias(nameof(CancelPendingOfAuthorAsync)), OneWay]
    Task CancelPendingOfAuthorAsync(Guid authorId);
}

/// <summary>The caller's composer drafts, keyed by the caller's user id.</summary>
[Alias("Argon.Grains.Interfaces.IMessageDraftsGrain")]
public interface IMessageDraftsGrain : IGrainWithGuidKey
{
    [Alias(nameof(SaveDraftAsync))]
    Task SaveDraftAsync(Guid spaceId, Guid channelId, string text, List<IMessageEntity> entities);

    [Alias(nameof(GetDraftAsync))]
    Task<MessageDraft?> GetDraftAsync(Guid spaceId, Guid channelId);

    /// <summary>Account erasure: drops the drafts in memory, unwritten ones included, and deletes the stored ones.</summary>
    [Alias(nameof(EraseAsync))]
    Task EraseAsync();
}
