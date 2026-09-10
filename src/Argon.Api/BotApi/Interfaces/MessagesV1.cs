namespace Argon.Api.BotApi.Interfaces;

using Argon.Features.BotApi;

[BotInterface("IMessages", 1)]
[BotDescription("Send messages and retrieve message history from channels.")]
public sealed class MessagesV1(IGrainFactory grains) : IBotInterface
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

    public sealed record MessageHistoryQuery(
        Guid  ChannelId,
        long? From  = null,
        int?  Limit = null);

    public sealed record MessageDto(
        long                 MessageId,
        Guid                 ChannelId,
        Guid                 SpaceId,
        string               Text,
        Guid                 CreatorId,
        DateTimeOffset       CreatedAt,
        long?                ReplyTo,
        List<IMessageEntity> Entities,
        List<ControlRowV1>?  Controls  = null,
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

        group.Post<SendMessageRequest, SendMessageResponse>("/Send")
           .Summary("Sends a text message to a channel. Include a unique randomId for deduplication. Optionally reply to another message via replyTo.")
           .Permission(ArgonEntitlement.SendMessages)
           .Handle(async (_, request) =>
            {
                var channel = grains.GetGrain<IChannelGrain>(request.ChannelId);
                var msgId   = await channel.SendMessage(
                    request.Text,
                    request.Entities ?? [],
                    request.RandomId,
                    request.ReplyTo,
                    request.Controls);

                return new SendMessageResponse(msgId);
            });

        group.Get<MessageHistoryQuery, MessageHistoryResponse>("/History")
           .Summary("Gets message history for a channel. Supports pagination via from (message ID) and limit (1–100, default 50).")
           .Permission(ArgonEntitlement.ReadHistory)
           .Handle(async (_, query) =>
            {
                var channel  = grains.GetGrain<IChannelGrain>(query.ChannelId);
                var messages = await channel.QueryMessages(query.From, Math.Clamp(query.Limit ?? 50, 1, 100));

                return new MessageHistoryResponse(
                    messages.Select(m => new MessageDto(
                        m.MessageId, m.ChannelId, m.SpaceId,
                        m.Text, m.CreatorId, m.CreatedAt,
                        m.Reply, m.Entities, m.Controls,
                        m.Reactions?.Select(r => new ReactionDto(r.Emoji, r.UserIds.Count, r.UserIds.Take(3).ToList())).ToList())).ToList());
            });
    }
}
