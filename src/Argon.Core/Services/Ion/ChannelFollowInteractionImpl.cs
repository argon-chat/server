namespace Argon.Services.Ion;

using Argon.Api.Features.Utils;
using ion.runtime;

public class ChannelFollowInteractionImpl(ILogger<IChannelFollowInteraction> logger) : IChannelFollowInteraction
{
    // Targets written to at once while a post fans out.
    private const int DeliveryConcurrency = 16;

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

    public async Task<IPublishMessageResult> PublishMessage(Guid spaceId, Guid channelId, long messageId, CancellationToken ct = default)
    {
        this.EnforceLockdown(LockdownSeverity.Critical);

        var prepared = await this.GetGrain<IChannelGrain>(channelId).PublishMessage(messageId);
        if (!prepared.IsSuccess)
            return new FailedPublishMessage(prepared.Error);

        var batch     = prepared.Value;
        var delivered = await DeliverAsync(batch);

        return new SuccessPublishMessage(batch.PublishedAt.UtcDateTime, delivered, batch.Targets.Count);
    }

    // From here rather than from a grain: GetGrain routes each target to the region that owns it.
    // A target that fails is logged and skipped; the publish itself has already happened.
    private async Task<int> DeliverAsync(CrosspostBatch batch)
    {
        var delivered = 0;

        await Parallel.ForEachAsync(batch.Targets, new ParallelOptions { MaxDegreeOfParallelism = DeliveryConcurrency },
            async (target, _) =>
            {
                try
                {
                    if (await this.GetGrain<IChannelGrain>(target).ReceiveCrosspostAsync(batch.Draft) is not null)
                        Interlocked.Increment(ref delivered);
                }
                catch (Exception e)
                {
                    logger.LogWarning(e, "Crosspost of message {MessageId} from channel {SourceChannelId} did not reach channel {TargetChannelId}",
                        batch.Draft.Source.SourceMessageId, batch.Draft.Source.SourceChannelId, target);
                }
            });

        return delivered;
    }
}
