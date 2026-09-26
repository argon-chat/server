namespace Argon.Services.Ion;

using ion.runtime;

public class ChannelInsightsInteractionImpl : IChannelInsightsInteraction
{
    public async Task<IReadCountResult> GetReadCount(Guid spaceId, Guid channelId, long messageId, CancellationToken ct = default)
        => await this.GetGrain<IChannelInsightsGrain>(channelId).GetReadCount(messageId, ct);

    public async Task<IonArray<ReadCountEntry>> GetReadCounts(Guid spaceId, Guid channelId, IonArray<long> messageIds,
        CancellationToken ct = default)
    {
        var grain   = this.GetGrain<IChannelInsightsGrain>(channelId);
        var entries = new List<ReadCountEntry>();
        foreach (var id in messageIds.Values.Distinct().Take(50))
            if (await grain.GetReadCount(id, ct) is SuccessReadCount ok)
                entries.Add(new ReadCountEntry(id, ok.readers, ok.members));
        return new IonArray<ReadCountEntry>(entries);
    }
}
