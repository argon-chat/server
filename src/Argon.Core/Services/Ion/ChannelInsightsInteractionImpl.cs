namespace Argon.Services.Ion;

using ion.runtime;

public class ChannelInsightsInteractionImpl : IChannelInsightsInteraction
{
    public async Task<IReadCountResult> GetReadCount(Guid spaceId, Guid channelId, long messageId, CancellationToken ct = default)
        => await this.GetGrain<IChannelInsightsGrain>(channelId).GetReadCount(messageId, ct);

    public async Task<IonArray<ReadCountEntry>> GetReadCounts(Guid spaceId, Guid channelId, IonArray<long> messageIds,
        CancellationToken ct = default)
        => new(await this.GetGrain<IChannelInsightsGrain>(channelId).GetReadCounts(messageIds.Values.ToList(), ct));
}
