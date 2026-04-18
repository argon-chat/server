namespace Argon.Api.BotApi.Interfaces;

using Argon.Features.BotApi;
using Argon.Features.BotApi.Contracts;
using Microsoft.AspNetCore.Mvc;

[BotInterface("IReactions", 1)]
[BotDescription("Add and remove emoji reactions on messages.")]
[StableContract("a192cbc985de63a76e4a7cd0413a774327a667ba5d44cf9eb81c579f448e7807")]
[BotRoute("POST",   "/Add",    RequestType = typeof(AddReactionRequest),    Description = "Adds a reaction to a message. Each user can react once per emoji. Maximum 20 unique emoji per message.", Permission = "AddReactions")]
[BotRoute("DELETE", "/Remove", RequestType = typeof(RemoveReactionRequest), Description = "Removes the bot's reaction from a message. Only the bot's own reaction can be removed.")]
[BotRoute("GET",    "/List",   ResponseType = typeof(ListReactionsResponse), Description = "Lists all reactions on a message. Returns emoji, count, and a preview of user IDs (up to 3).")]
[BotRoute("POST",  "/BatchGet", RequestType = typeof(BatchGetReactionsRequest), ResponseType = typeof(BatchGetReactionsResponse), Description = "Returns current reactions for up to 50 messages in a single channel. Ideal for refreshing visible messages after reconnect.")]
[BotError("/Add",    403, "not_a_member",          "Bot is not a member of this space.")]
[BotError("/Add",    403, "insufficient_permissions", "Bot does not have the AddReactions permission.")]
[BotError("/Add",    404, "message_not_found",     "Message does not exist in this channel.")]
[BotError("/Add",    409, "already_reacted",       "Bot has already reacted with this emoji.")]
[BotError("/Add",    422, "reaction_limit_reached", "Maximum 20 unique emoji per message.")]
[BotError("/Remove", 404, "message_not_found",     "Message does not exist in this channel.")]
[BotError("/Remove", 404, "reaction_not_found",    "Bot has not reacted with this emoji.")]
[BotError("/List",   404, "message_not_found",     "Message does not exist in this channel.")]
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

    public void MapRoutes(RouteGroupBuilder group)
    {
        group.AddEndpointFilter<BotOrleansPropagationFilter>();
        group.RequireRateLimiting("Bot_IReactions");

        group.MapPost("/Add", async (HttpContext ctx, [FromBody] AddReactionRequest request) =>
        {
            var channel = grains.GetGrain<IChannelGrain>(request.ChannelId);
            var result  = await channel.AddReaction(request.MessageId, request.Emoji);

            return result switch
            {
                SuccessAddReaction                                                          => Results.Ok(),
                FailedAddReaction { error: AddReactionError.MESSAGE_NOT_FOUND }             => Results.NotFound(new { error = "message_not_found" }),
                FailedAddReaction { error: AddReactionError.ALREADY_REACTED }               => Results.Conflict(new { error = "already_reacted" }),
                FailedAddReaction { error: AddReactionError.REACTION_LIMIT_REACHED }        => Results.UnprocessableEntity(new { error = "reaction_limit_reached" }),
                FailedAddReaction { error: AddReactionError.INSUFFICIENT_PERMISSIONS }      => Results.Json(new { error = "insufficient_permissions" }, statusCode: StatusCodes.Status403Forbidden),
                _                                                                           => Results.StatusCode(500)
            };
        });

        group.MapDelete("/Remove", async (HttpContext ctx, [FromBody] RemoveReactionRequest request) =>
        {
            var channel = grains.GetGrain<IChannelGrain>(request.ChannelId);
            var result  = await channel.RemoveReaction(request.MessageId, request.Emoji);

            return result switch
            {
                SuccessRemoveReaction                                                       => Results.Ok(),
                FailedRemoveReaction { error: RemoveReactionError.MESSAGE_NOT_FOUND }       => Results.NotFound(new { error = "message_not_found" }),
                FailedRemoveReaction { error: RemoveReactionError.REACTION_NOT_FOUND }      => Results.NotFound(new { error = "reaction_not_found" }),
                _                                                                           => Results.StatusCode(500)
            };
        });

        group.MapGet("/List", async (HttpContext ctx, [FromQuery] Guid channelId, [FromQuery] long messageId) =>
        {
            var channel  = grains.GetGrain<IChannelGrain>(channelId);
            var messages = await channel.QueryMessages(messageId, 1);
            var message  = messages.FirstOrDefault(m => m.MessageId == messageId);

            if (message is null)
                return Results.NotFound(new { error = "message_not_found" });

            var reactions = message.Reactions?
               .Select(r => new ReactionDto(r.Emoji, r.UserIds.Count, r.UserIds.Take(3).ToList()))
               .ToList() ?? [];

            return Results.Ok(new ListReactionsResponse(reactions));
        });

        group.MapPost("/BatchGet", async (HttpContext ctx, [FromBody] BatchGetReactionsRequest request) =>
        {
            var channel = grains.GetGrain<IChannelGrain>(request.ChannelId);
            var dict    = await channel.BatchGetReactions(request.MessageIds);

            var messages = dict.Select(kv => new MessageReactionsDto(
                kv.Key,
                kv.Value.Select(r => new ReactionDto(r.emoji, r.count, r.userIds.Values.Take(3).ToList())).ToList()
            )).ToList();

            return Results.Ok(new BatchGetReactionsResponse(messages));
        });
    }
}
