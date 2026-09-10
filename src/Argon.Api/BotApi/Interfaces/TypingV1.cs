namespace Argon.Api.BotApi.Interfaces;

using Argon.Features.BotApi;

[BotInterface("ITyping", 1)]
[BotDescription("Send typing indicators to channels. Typing status automatically expires after 8 seconds.")]
public sealed class TypingV1(IGrainFactory grains) : IBotInterface
{
    public sealed record SendTypingRequest(
        Guid    ChannelId,
        string? Kind = null);

    public sealed record StopTypingRequest(
        Guid ChannelId);

    private static readonly BotError InvalidKind = new(400, "invalid_kind",
        "Unknown typing kind. Supported: typing, thinking, uploading, searching.");

    private static readonly Dictionary<string, TypingKind> KindMap = new(StringComparer.OrdinalIgnoreCase)
    {
        ["typing"]    = TypingKind.TYPING,
        ["thinking"]  = TypingKind.THINKING,
        ["uploading"] = TypingKind.UPLOADING,
        ["searching"] = TypingKind.SEARCHING,
    };

    public void MapRoutes(RouteGroupBuilder group)
    {
        group.AddEndpointFilter<BotOrleansPropagationFilter>();
        group.RequireRateLimiting("Bot_ITyping");

        group.Post<SendTypingRequest>("/Start")
           .Summary("Triggers a typing indicator in a channel. The indicator auto-expires after 8 seconds. Call repeatedly to keep it active. Supported kinds: typing, thinking, uploading, searching.")
           .Permission(ArgonEntitlement.SendMessages)
           .Throws(InvalidKind)
           .Handle(async (_, request) =>
            {
                var kind = TypingKind.TYPING;

                if (request.Kind is not null && !KindMap.TryGetValue(request.Kind, out kind))
                    throw InvalidKind.Raise(
                        $"Unknown typing kind '{request.Kind}'. Supported: typing, thinking, uploading, searching.");

                await grains.GetGrain<IChannelGrain>(request.ChannelId).OnBotTypingEmit(kind);
            });

        group.Post<StopTypingRequest>("/Stop")
           .Summary("Explicitly stops the typing indicator in a channel. Optional — the indicator expires automatically after 8 seconds.")
           .Handle(async (_, request)
                => await grains.GetGrain<IChannelGrain>(request.ChannelId).OnTypingStopEmit());
    }
}
