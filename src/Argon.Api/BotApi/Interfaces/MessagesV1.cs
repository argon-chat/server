namespace Argon.Api.BotApi.Interfaces;

using Argon.Features.BotApi;
using Argon.Features.BotApi.Contracts;

[BotInterface("IMessages", 1)]
[BotDescription("Send messages and retrieve message history from channels.")]
[StableContract("bd1b0ab376c3db85c5c8932b02b13938e0e6de5e69662afc928869bf3b54eba6")]
[BotRoute("POST", "/Send",    RequestType = typeof(SendMessageRequest), ResponseType = typeof(SendMessageResponse), Description = "Sends a text message to a channel. Include a unique randomId for deduplication. Optionally reply to another message via replyTo.", Permission = "SendMessages")]
[BotRoute("GET",  "/History", ResponseType = typeof(MessageHistoryResponse), Description = "Gets message history for a channel. Pass channelId as a query parameter. Supports pagination via from (message ID) and limit (1–100, default 50).", Permission = "ReadMessages")][BotError("/Send", 403, "not_a_member", "Bot is not a member of this space.")]
[BotError("/History", 403, "not_a_member", "Bot is not a member of this space.")]public sealed class MessagesV1(IGrainFactory grains) : IBotInterface
{
    public sealed record SendMessageRequest(
        Guid                  ChannelId,
        string                Text,
        long                  RandomId,
        long?                 ReplyTo  = null,
        List<IMessageEntity>? Entities = null,
        List<ControlRowV1>?   Controls = null);

    public sealed record SendMessageResponse(
        long MessageId);

    public sealed record MessageDto(
        long             MessageId,
        Guid             ChannelId,
        Guid             SpaceId,
        string           Text,
        Guid             CreatorId,
        DateTimeOffset   CreatedAt,
        long?            ReplyTo,
        List<IMessageEntity> Entities,
        List<ControlRowV1>?  Controls = null,
        List<ReactionDto>?   Reactions = null);

    public sealed record ReactionDto(
        string     Emoji,
        int        Count,
        List<Guid> UserIds);

    public sealed record MessageHistoryResponse(
        List<MessageDto> Messages);

    public void MapRoutes(RouteGroupBuilder group)
    {
        group.AddEndpointFilter<BotOrleansPropagationFilter>();
        group.RequireRateLimiting("Bot_IMessages");

        group.MapPost("/Send", async (HttpContext ctx, SendMessageRequest request) =>
        {
            var channel = grains.GetGrain<IChannelGrain>(request.ChannelId);
            var msgId   = await channel.SendMessage(
                request.Text,
                request.Entities ?? [],
                request.RandomId,
                request.ReplyTo,
                request.Controls);

            return Results.Ok(new SendMessageResponse(msgId));
        });

        group.MapGet("/History", async (HttpContext ctx, Guid channelId, long? from, int? limit) =>
        {
            var channel  = grains.GetGrain<IChannelGrain>(channelId);
            var messages = await channel.QueryMessages(from, Math.Clamp(limit ?? 50, 1, 100));

            return Results.Ok(new MessageHistoryResponse(
                messages.Select(m => new MessageDto(
                    m.MessageId, m.ChannelId, m.SpaceId,
                    m.Text, m.CreatorId, m.CreatedAt,
                    m.Reply, m.Entities, m.Controls,
                    m.Reactions?.Select(r => new ReactionDto(r.Emoji, r.UserIds.Count, r.UserIds.Take(3).ToList())).ToList())).ToList()));
        });
    }
}
