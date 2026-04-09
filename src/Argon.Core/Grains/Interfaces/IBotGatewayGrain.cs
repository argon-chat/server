namespace Argon.Grains.Interfaces;

using Argon.Features.BotApi;

/// <summary>
/// Gateway grain for a bot's SSE event stream.
/// Keyed by BotAsUserId. Manages event buffering, intent filtering, and lifecycle.
/// </summary>
[Alias("Argon.Grains.Interfaces.IBotGatewayGrain")]
public interface IBotGatewayGrain : IGrainWithGuidKey
{
    /// <summary>
    /// Begins a bot session with the specified intents.
    /// Returns the list of space IDs the bot is a member of.
    /// </summary>
    [Alias(nameof(ConnectAsync))]
    Task<List<Guid>> ConnectAsync(BotIntent intents);

    /// <summary>
    /// Ends the bot session and cleans up resources.
    /// </summary>
    [Alias(nameof(DisconnectAsync))]
    Task DisconnectAsync();

    /// <summary>
    /// Dispatches a domain event to this bot. The grain will filter by intents
    /// and write to the SSE output channel.
    /// </summary>
    [Alias(nameof(DispatchEventAsync))]
    Task DispatchEventAsync(BotSseEvent evt, BotIntent requiredIntent);

    /// <summary>
    /// Returns true if the bot currently has an active SSE session.
    /// </summary>
    [Alias(nameof(IsConnectedAsync))]
    Task<bool> IsConnectedAsync();

    /// <summary>
    /// Gets the next batch of events since the given event ID (for resume support).
    /// </summary>
    [Alias(nameof(GetEventsSinceAsync))]
    Task<List<BotSseEvent>> GetEventsSinceAsync(string lastEventId);
}
