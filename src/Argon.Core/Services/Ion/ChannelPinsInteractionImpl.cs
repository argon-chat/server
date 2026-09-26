namespace Argon.Services.Ion;

using Argon.Core.Grains.Interfaces;
using ion.runtime;

public sealed class ChannelPinsInteractionImpl : IChannelPinsInteraction
{
    public async Task<IPinMessageResult> PinMessage(Guid spaceId, Guid channelId, long messageId, CancellationToken ct = default)
        => await this.GetGrain<IChannelGrain>(channelId).PinMessage(messageId, ct);

    public async Task<IUnpinMessageResult> UnpinMessage(Guid spaceId, Guid channelId, long messageId, CancellationToken ct = default)
        => await this.GetGrain<IChannelGrain>(channelId).UnpinMessage(messageId, ct);

    public async Task<IonArray<PinnedMessage>> GetPinnedMessages(Guid spaceId, Guid channelId, CancellationToken ct = default)
        => new(await this.GetGrain<IChannelGrain>(channelId).GetPinnedMessages(ct));
}
