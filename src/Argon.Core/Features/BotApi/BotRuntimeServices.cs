namespace Argon.Features.BotApi;

using Microsoft.Extensions.DependencyInjection.Extensions;

/// <summary>
/// Services the bot runtime needs wherever bot traffic is served or bot events are published.
/// </summary>
/// <remarks>
/// These used to be registered inside <c>AddSignalRAppHub</c> — the SignalR hub feature registering
/// the bot stack, which worked only because one process did both. Splitting the bot API onto its own
/// role made it visible: <c>botapi</c> mapped its endpoints and then failed to activate
/// <c>InteractionsV1</c> because <c>InteractionContextStore</c> was never registered. Both the hub
/// and the bot API pull this in, and calling it twice is idempotent.
/// </remarks>
public static class BotRuntimeServices
{
    public static WebApplicationBuilder AddBotRuntimeServices(this WebApplicationBuilder builder)
    {
        builder.Services.TryAddSingleton<BotSseEventSerializer>();
        builder.Services.TryAddSingleton<BotUserCache>();
        builder.Services.TryAddSingleton<UserLocaleRegistry>();
        builder.Services.TryAddSingleton<InteractionContextStore>();
        builder.Services.TryAddSingleton<BotEventPublisher>();
        builder.Services.TryAddScoped<InteractionResponsePusher>();

        return builder;
    }
}
