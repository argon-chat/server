namespace Argon.Grains.Interfaces;

using Argon.Features.BotApi;

/// <summary>
/// Gateway grain for a bot's SSE event stream.
/// Keyed by BotAsUserId. Manages NATS JetStream consumers for bot events per space.
/// </summary>
[Alias("Argon.Grains.Interfaces.IBotGatewayGrain")]
public interface IBotGatewayGrain : IGrainWithGuidKey
{
    /// <summary>
    /// Begins a bot session with the specified intents.
    /// Creates NATS consumers for all bot spaces.
    /// Sets bot status to Online.
    /// Returns per-space info including effective entitlements.
    /// </summary>
    [Alias(nameof(ConnectAsync))]
    Task<List<BotSpaceInfo>> ConnectAsync(BotIntent intents);

    /// <summary>
    /// Ends the bot session. Deletes NATS consumers.
    /// Sets bot status to Offline.
    /// </summary>
    [Alias(nameof(DisconnectAsync))]
    Task DisconnectAsync();

    /// <summary>
    /// Returns true if the bot currently has an active SSE session.
    /// </summary>
    [Alias(nameof(IsConnectedAsync))]
    Task<bool> IsConnectedAsync();

    /// <summary>
    /// Subscribe to events for a specific space (e.g. after bot is installed to a new space).
    /// Creates a NATS consumer for this space, sets bot Online in the space.
    /// </summary>
    [Alias(nameof(SubscribeToSpace))]
    Task SubscribeToSpace(Guid spaceId);

    /// <summary>
    /// Unsubscribe from a space's events (e.g. after bot is uninstalled).
    /// Deletes the NATS consumer, sets bot Offline in the space.
    /// </summary>
    [Alias(nameof(UnsubscribeFromSpace))]
    Task UnsubscribeFromSpace(Guid spaceId);

    /// <summary>
    /// Consume pending events from all subscribed spaces.
    /// Filters by bot's intents. Event IDs are NATS stream sequences (stable, monotonic).
    /// </summary>
    [Alias(nameof(ConsumeEventsAsync))]
    Task<List<BotSseEvent>> ConsumeEventsAsync(int maxCount);

    /// <summary>
    /// Returns the current cursor string encoding last consumed NATS sequences.
    /// Used as heartbeat event ID so clients can resume from the right position.
    /// </summary>
    [Alias(nameof(GetCursor))]
    Task<string> GetCursor();
}

/// <summary>
/// Per-space info returned on bot connect.
/// Includes effective (granted) entitlements and whether the bot has pending approval for expanded permissions.
/// </summary>
[GenerateSerializer, Immutable]
public sealed record BotSpaceInfo(
    [property: Id(0)] Guid             SpaceId,
    [property: Id(1)] ArgonEntitlement GrantedEntitlements,
    [property: Id(2)] bool             PendingApproval);
