namespace Argon.Services.Ion;

using Argon.Api.Features.Utils;
using ion.runtime;

public class ChannelFollowInteractionImpl : IChannelFollowInteraction
{
    public async Task<IFollowChannelResult> FollowChannel(Guid spaceId, Guid channelId, Guid targetSpaceId, Guid targetChannelId,
        CancellationToken ct = default)
    {
        this.EnforceLockdown(LockdownSeverity.Critical);
        return await this
           .GetGrain<IChannelFollowGrain>(targetChannelId)
           .FollowAsync(this.GetUserId(), spaceId, channelId, targetSpaceId);
    }

    public async Task<IonArray<ChannelFollowLink>> GetFollowers(Guid spaceId, Guid channelId, CancellationToken ct = default)
        => new(await this.GetGrain<IChannelFollowGrain>(channelId).GetFollowersAsync(this.GetUserId(), spaceId));

    public async Task<IonArray<ChannelFollowLink>> GetFollowedSources(Guid spaceId, Guid channelId, CancellationToken ct = default)
        => new(await this.GetGrain<IChannelFollowGrain>(channelId).GetFollowedSourcesAsync(this.GetUserId(), spaceId));

    public async Task<IRemoveFollowResult> RemoveFollow(Guid spaceId, Guid channelId, Guid followId, CancellationToken ct = default)
        => await this.GetGrain<IChannelFollowGrain>(channelId).RemoveAsync(this.GetUserId(), spaceId, followId);

    // Delivery runs in the source's ICrosspostDeliveryGrain after this returns, so deliveredCount is 0.
    public async Task<IPublishMessageResult> PublishMessage(Guid spaceId, Guid channelId, long messageId, CancellationToken ct = default)
    {
        this.EnforceLockdown(LockdownSeverity.Critical);

        var published = await this.GetGrain<IChannelGrain>(channelId).PublishMessage(messageId);
        if (!published.IsSuccess)
            return new FailedPublishMessage(published.Error);

        return new SuccessPublishMessage(published.Value.PublishedAt.UtcDateTime, 0, published.Value.TargetCount);
    }
}
