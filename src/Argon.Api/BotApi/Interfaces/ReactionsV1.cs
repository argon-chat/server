namespace Argon.Api.BotApi.Interfaces;

using Argon.Features.BotApi;

[BotInterface("IReactions", 1)]
[BotDescription("Add and remove emoji reactions on messages.")]
public sealed class ReactionsV1(IGrainFactory grains) : IBotInterface
{
    public sealed record AddReactionRequest(
        Guid   ChannelId,
        long   MessageId,
        string Emoji);

    public sealed record RemoveReactionRequest(
        Guid   ChannelId,
        long   MessageId,
        string Emoji);

    public sealed record ListReactionsQuery(
        Guid ChannelId,
        long MessageId);

    public sealed record ReactionDto(
        string     Emoji,
        int        Count,
        List<Guid> UserIds);

    public sealed record ListReactionsResponse(
        List<ReactionDto> Reactions);

    public sealed record BatchGetReactionsRequest(
        Guid       ChannelId,
        List<long> MessageIds);

    public sealed record MessageReactionsDto(
        long              MessageId,
        List<ReactionDto> Reactions);

    public sealed record BatchGetReactionsResponse(
        List<MessageReactionsDto> Messages);

    private static readonly BotError MessageNotFound       = new(404, "message_not_found", "Message does not exist in this channel.");
    private static readonly BotError ReactionNotFound      = new(404, "reaction_not_found", "Bot has not reacted with this emoji.");
    private static readonly BotError AlreadyReacted        = new(409, "already_reacted", "Bot has already reacted with this emoji.");
    private static readonly BotError ReactionLimitReached  = new(422, "reaction_limit_reached", "Maximum 20 unique emoji per message.");
    private static readonly BotError InsufficientRights    = new(403, "insufficient_permissions", "Bot does not have the AddReactions permission.");

    public void MapRoutes(RouteGroupBuilder group)
    {
        group.AddEndpointFilter<BotOrleansPropagationFilter>();
        group.RequireRateLimiting("Bot_IReactions");

        group.Post<AddReactionRequest>("/Add")
           .Summary("Adds a reaction to a message. Each user can react once per emoji. Maximum 20 unique emoji per message.")
           .Permission(ArgonEntitlement.AddReactions)
           .Throws(MessageNotFound)
           .Throws(AlreadyReacted)
           .Throws(ReactionLimitReached)
           .Throws(InsufficientRights)
           .Handle(async (_, request) =>
            {
                var result = await grains.GetGrain<IChannelGrain>(request.ChannelId)
                   .AddReaction(request.MessageId, request.Emoji);

                if (result is FailedAddReaction failure)
                    throw (failure.error switch
                    {
                        AddReactionError.MESSAGE_NOT_FOUND        => MessageNotFound,
                        AddReactionError.ALREADY_REACTED          => AlreadyReacted,
                        AddReactionError.REACTION_LIMIT_REACHED   => ReactionLimitReached,
                        AddReactionError.INSUFFICIENT_PERMISSIONS => InsufficientRights,
                        _                                         => throw new InvalidOperationException(
                            $"unhandled AddReactionError {failure.error}")
                    }).Raise();
            });

        group.Delete<RemoveReactionRequest>("/Remove")
           .FromBody()
           .Summary("Removes the bot's reaction from a message. Only the bot's own reaction can be removed.")
           .Throws(MessageNotFound)
           .Throws(ReactionNotFound)
           .Handle(async (_, request) =>
            {
                var result = await grains.GetGrain<IChannelGrain>(request.ChannelId)
                   .RemoveReaction(request.MessageId, request.Emoji);

                if (result is FailedRemoveReaction failure)
                    throw (failure.error switch
                    {
                        RemoveReactionError.MESSAGE_NOT_FOUND  => MessageNotFound,
                        RemoveReactionError.REACTION_NOT_FOUND => ReactionNotFound,
                        _                                      => throw new InvalidOperationException(
                            $"unhandled RemoveReactionError {failure.error}")
                    }).Raise();
            });

        group.Get<ListReactionsQuery, ListReactionsResponse>("/List")
           .Summary("Lists all reactions on a message. Returns emoji, count, and a preview of user IDs (up to 3).")
           .Throws(MessageNotFound)
           .Handle(async (_, query) =>
            {
                var messages = await grains.GetGrain<IChannelGrain>(query.ChannelId).QueryMessages(query.MessageId, 1);
                var message  = messages.FirstOrDefault(m => m.MessageId == query.MessageId)
                            ?? throw MessageNotFound.Raise();

                return new ListReactionsResponse(message.Reactions?
                   .Select(r => new ReactionDto(r.Emoji, r.UserIds.Count, r.UserIds.Take(3).ToList()))
                   .ToList() ?? []);
            });

        group.Post<BatchGetReactionsRequest, BatchGetReactionsResponse>("/BatchGet")
           .Summary("Returns current reactions for up to 50 messages in a single channel. Ideal for refreshing visible messages after reconnect.")
           .Handle(async (_, request) =>
            {
                var byMessage = await grains.GetGrain<IChannelGrain>(request.ChannelId)
                   .BatchGetReactions(request.MessageIds);

                return new BatchGetReactionsResponse(byMessage.Select(kv => new MessageReactionsDto(
                    kv.Key,
                    kv.Value.Select(r => new ReactionDto(r.emoji, r.count, r.userIds.Values.Take(3).ToList())).ToList()))
                   .ToList());
            });
    }
}
