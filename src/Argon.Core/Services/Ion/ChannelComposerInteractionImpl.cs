namespace Argon.Services.Ion;

using ion.runtime;

public class ChannelComposerInteractionImpl : IChannelComposerInteraction
{
    public async Task<ISchedulePostResult> SchedulePost(Guid spaceId, Guid channelId, string text, IonArray<IMessageEntity> entities,
        DateTimeOffset publishAt, CancellationToken ct = default)
    {
        this.EnforceLockdown(LockdownSeverity.Critical);
        return await this.GetGrain<IChannelComposerGrain>(channelId)
           .SchedulePostAsync(spaceId, text, entities.Values.ToList(), publishAt);
    }

    public async Task<ISchedulePostResult> ReschedulePost(Guid spaceId, Guid channelId, Guid postId, DateTimeOffset publishAt,
        CancellationToken ct = default)
    {
        this.EnforceLockdown(LockdownSeverity.Critical);
        return await this.GetGrain<IChannelComposerGrain>(channelId).ReschedulePostAsync(spaceId, postId, publishAt);
    }

    public async Task<bool> CancelScheduledPost(Guid spaceId, Guid channelId, Guid postId, CancellationToken ct = default)
        => await this.GetGrain<IChannelComposerGrain>(channelId).CancelScheduledPostAsync(spaceId, postId);

    public async Task<IonArray<ScheduledPost>> GetScheduledPosts(Guid spaceId, Guid channelId, CancellationToken ct = default)
        => new(await this.GetGrain<IChannelComposerGrain>(channelId).GetScheduledPostsAsync(spaceId));

    public async Task SaveDraft(Guid spaceId, Guid channelId, string text, IonArray<IMessageEntity> entities, CancellationToken ct = default)
        => await this.GetGrain<IMessageDraftsGrain>(this.GetUserId())
           .SaveDraftAsync(spaceId, channelId, text, entities.Values.ToList());

    public async Task<MessageDraft?> GetDraft(Guid spaceId, Guid channelId, CancellationToken ct = default)
        => await this.GetGrain<IMessageDraftsGrain>(this.GetUserId()).GetDraftAsync(spaceId, channelId);
}
