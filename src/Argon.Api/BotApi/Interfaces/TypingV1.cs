namespace Argon.Api.BotApi.Interfaces;

using Argon.Features.BotApi;
using Argon.Features.BotApi.Contracts;

[BotInterface("ITyping", 1)]
[BotDescription("Send typing indicators to channels. Typing status automatically expires after 8 seconds.")]
[StableContract("1665131832ec0d80955b84f0c1508d9ee8c83332b1c012885d2c79b5ac166c21")]
[BotRoute("POST", "/Start", RequestType = typeof(SendTypingRequest), Description = "Triggers a typing indicator in a channel. The indicator auto-expires after 8 seconds. Call repeatedly to keep it active. Supported kinds: typing, thinking, uploading, searching.", Permission = "SendMessages")]
[BotRoute("POST", "/Stop",  RequestType = typeof(StopTypingRequest), Description = "Explicitly stops the typing indicator in a channel. Optional — the indicator expires automatically after 8 seconds.")]
[BotError("/Start", 403, "not_a_member", "Bot is not a member of this space.")]
[BotError("/Stop",  403, "not_a_member", "Bot is not a member of this space.")]
public sealed class TypingV1(IGrainFactory grains) : IBotInterface
{
    public sealed record SendTypingRequest(
        Guid    ChannelId,
        string? Kind = null);

    public sealed record StopTypingRequest(
        Guid ChannelId);

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

        group.MapPost("/Start", async (HttpContext ctx, SendTypingRequest request) =>
        {
            var kind = TypingKind.TYPING;
            if (request.Kind is not null)
            {
                if (!KindMap.TryGetValue(request.Kind, out kind))
                    return Results.Json(
                        new BotApiError("invalid_kind", $"Unknown typing kind '{request.Kind}'. Supported: typing, thinking, uploading, searching."),
                        statusCode: StatusCodes.Status400BadRequest);
            }

            var channel = grains.GetGrain<IChannelGrain>(request.ChannelId);
            await channel.OnBotTypingEmit(kind);
            return Results.Ok();
        });

        group.MapPost("/Stop", async (HttpContext ctx, StopTypingRequest request) =>
        {
            var channel = grains.GetGrain<IChannelGrain>(request.ChannelId);
            await channel.OnTypingStopEmit();
            return Results.Ok();
        });
    }
}
