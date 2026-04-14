namespace Argon.Api.BotApi.Interfaces;

using Argon.Features.BotApi;
using Argon.Features.BotApi.Contracts;

[BotInterface("IInteractions", 1)]
[BotDescription("Respond to slash-command, control, and select interactions. Supports ack, defer, modal, reply, and edit.")]
[StableContract("3a08148ef4f12b63d7b58dbda29862a91ee4d09789d33f504910071fe527117f")]
[BotRoute("POST",  "/Reply",       RequestType = typeof(ReplyRequest),       ResponseType = typeof(ReplyResponse), Description = "Reply to an interaction by sending a message to the channel. Optionally reply to a specific message via replyTo.")]
[BotRoute("PATCH", "/EditMessage", RequestType = typeof(EditMessageRequest), Description = "Edit the text or controls of a message previously sent by this bot.")]
[BotRoute("POST",  "/Ack",         RequestType = typeof(AckRequest),         Description = "Acknowledge an interaction. The client shows a brief confirmation.")]
[BotRoute("POST",  "/Defer",       RequestType = typeof(DeferRequest),       Description = "Defer an interaction. The client shows a loading state until the bot follows up.")]
[BotRoute("POST",  "/Modal",       RequestType = typeof(ModalRequest),       ResponseType = typeof(ModalResponse), Description = "Show a modal dialog to the user who triggered the interaction.")]
[BotError("/Reply",       403, "not_a_member", "Bot is not a member of this space.")]
[BotError("/EditMessage", 404, "message_not_found", "Message does not exist or was not sent by this bot.")]
[BotError("/Ack",         404, "interaction_not_found", "Interaction does not exist or has expired.")]
[BotError("/Defer",       404, "interaction_not_found", "Interaction does not exist or has expired.")]
[BotError("/Modal",       404, "interaction_not_found", "Interaction does not exist or has expired.")]
[BotError("/Modal",       400, "validation_error", "Modal definition is invalid.")]
public sealed class InteractionsV1(
    IGrainFactory              grains,
    InteractionContextStore    interactionStore,
    InteractionResponsePusher  pusher) : IBotInterface
{
    public sealed record ReplyRequest(
        Guid                  ChannelId,
        string                Text,
        long                  RandomId,
        long?                 ReplyTo  = null,
        List<IMessageEntity>? Entities = null,
        List<ControlRowV1>?   Controls = null);

    public sealed record ReplyResponse(long MessageId);

    public sealed record EditMessageRequest(
        Guid                  ChannelId,
        long                  MessageId,
        string?               Text     = null,
        List<ControlRowV1>?   Controls = null);

    public sealed record AckRequest(Guid InteractionId);
    public sealed record DeferRequest(Guid InteractionId);

    public sealed record ModalRequest(
        Guid               InteractionId,
        ModalDefinitionV1  Modal);

    public sealed record ModalResponse(Guid ModalInteractionId);

    public void MapRoutes(RouteGroupBuilder group)
    {
        group.AddEndpointFilter<BotOrleansPropagationFilter>();
        group.RequireRateLimiting("Bot_IInteractions");

        group.MapPost("/Reply", async (HttpContext ctx, ReplyRequest request) =>
        {
            var channel = grains.GetGrain<IChannelGrain>(request.ChannelId);
            var msgId = await channel.SendMessage(
                request.Text,
                request.Entities ?? [],
                request.RandomId,
                request.ReplyTo,
                request.Controls);

            return Results.Ok(new ReplyResponse(msgId));
        });

        group.MapPatch("/EditMessage", async (HttpContext ctx, EditMessageRequest request) =>
        {
            var botUserId = ctx.GetBotAsUserId();
            var channel   = grains.GetGrain<IChannelGrain>(request.ChannelId);

            await channel.EditBotMessage(request.MessageId, botUserId, request.Text, request.Controls);
            return Results.Ok();
        });

        group.MapPost("/Ack", async (HttpContext ctx, AckRequest request) =>
        {
            var interaction = interactionStore.TryPeek(request.InteractionId);
            if (interaction is null)
                return Results.NotFound(new { error = "interaction_not_found" });

            await pusher.PushAckAsync(request.InteractionId, interaction.UserId);
            return Results.Ok();
        });

        group.MapPost("/Defer", async (HttpContext ctx, DeferRequest request) =>
        {
            var interaction = interactionStore.TryPeek(request.InteractionId);
            if (interaction is null)
                return Results.NotFound(new { error = "interaction_not_found" });

            await pusher.PushDeferredAsync(request.InteractionId, interaction.UserId);
            return Results.Ok();
        });

        group.MapPost("/Modal", async (HttpContext ctx, ModalRequest request) =>
        {
            var interaction = interactionStore.TryConsume(request.InteractionId);
            if (interaction is null)
                return Results.NotFound(new { error = "interaction_not_found" });

            request.Modal.Validate();

            var modalInteractionId = Guid.NewGuid();
            interactionStore.Register(
                modalInteractionId,
                interaction.UserId,
                interaction.ChannelId,
                interaction.SpaceId,
                interaction.BotAppId);

            await pusher.PushShowModalAsync(modalInteractionId, interaction.UserId, request.Modal);
            return Results.Ok(new ModalResponse(modalInteractionId));
        });
    }
}
