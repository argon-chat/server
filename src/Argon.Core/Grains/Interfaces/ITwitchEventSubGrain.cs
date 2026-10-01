namespace Argon.Grains.Interfaces;

using Argon.Features.Integrations.Connections.Twitch;

/// <summary>
/// The server side of Twitch EventSub: verifies what the webhook received, keeps the subscriptions
/// a streaming status needs, and routes a notification to the streamer's presence grain.
/// </summary>
/// <remarks>
/// A stateless worker keyed <c>Guid.Empty</c>. The signing secret, like every provider secret,
/// is on the silo; the entry point hands the raw message over and answers with whatever this says.
/// Twitch wants its answer within ten seconds, which one grain call and one cache round trip leave
/// plenty of.
/// </remarks>
[Alias("Argon.Grains.Interfaces.ITwitchEventSubGrain")]
public interface ITwitchEventSubGrain : IGrainWithGuidKey
{
    [Alias(nameof(HandleAsync))]
    Task<EventSubAnswer> HandleAsync(TwitchEventSubMessage message, CancellationToken ct = default);

    /// <summary>Subscribes the broadcaster's online and offline events; idempotent.</summary>
    [Alias(nameof(EnsureSubscriptionsAsync))]
    Task EnsureSubscriptionsAsync(string broadcasterId, CancellationToken ct = default);

    [Alias(nameof(RemoveSubscriptionsAsync))]
    Task RemoveSubscriptionsAsync(string broadcasterId, CancellationToken ct = default);
}
