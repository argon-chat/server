namespace Argon.Api.BotApi.Interfaces;

using Argon.Features.BotApi;

[BotInterface("IInteractions", 1)]
[BotDescription("Respond to slash-command, control, and select interactions. Supports ack, defer, modal, reply, and edit.")]
// InteractionResponsePusher is not a constructor dependency, unlike everything else here. Bot
// interfaces are constructed once while routes are being mapped, and the closures the handlers make
// out of the constructor parameters outlive every request — so a scoped service taken that way would
// be one request's AppHubServer serving all of them. It is resolved per request instead.
public sealed class InteractionsV1(
    IGrainFactory           grains,
    InteractionContextStore interactionStore) : IBotInterface
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
        Guid                ChannelId,
        long                MessageId,
        string?             Text     = null,
        List<ControlRowV1>? Controls = null);

    public sealed record AckRequest(Guid InteractionId);

    public sealed record DeferRequest(Guid InteractionId);

    public sealed record ModalRequest(
        Guid              InteractionId,
        ModalDefinitionV1 Modal);

    public sealed record ModalResponse(Guid ModalInteractionId);

    private static readonly BotError InteractionNotFound = new(404, "interaction_not_found",
        "Interaction does not exist or has expired.");

    private static readonly BotError ValidationError = new(400, "validation_error",
        "Modal definition is invalid.");

    public void MapRoutes(RouteGroupBuilder group)
    {
        group.AddEndpointFilter<BotOrleansPropagationFilter>();
        group.RequireRateLimiting("Bot_IInteractions");

        group.Post<ReplyRequest, ReplyResponse>("/Reply")
           .Summary("Reply to an interaction by sending a message to the channel. Optionally reply to a specific message via replyTo.")
           .Permission(ArgonEntitlement.SendMessages)
           .Handle(async (_, request) =>
            {
                var msgId = await grains.GetGrain<IChannelGrain>(request.ChannelId).SendMessage(
                    request.Text,
                    request.Entities ?? [],
                    request.RandomId,
                    request.ReplyTo,
                    request.Controls);

                return new ReplyResponse(msgId);
            });

        group.Patch<EditMessageRequest>("/EditMessage")
           .Summary("Edit the text or controls of a message previously sent by this bot.")
           .Handle(async (ctx, request)
                => await grains.GetGrain<IChannelGrain>(request.ChannelId).EditBotMessage(
                    request.MessageId, ctx.GetBotAsUserId(), request.Text, request.Controls));

        group.Post<AckRequest>("/Ack")
           .Summary("Acknowledge an interaction. The client shows a brief confirmation.")
           .Throws(InteractionNotFound)
           .Handle(async (ctx, request) =>
            {
                var interaction = interactionStore.TryPeek(request.InteractionId)
                               ?? throw InteractionNotFound.Raise();

                await Pusher(ctx).PushAckAsync(request.InteractionId, interaction.UserId);
            });

        group.Post<DeferRequest>("/Defer")
           .Summary("Defer an interaction. The client shows a loading state until the bot follows up.")
           .Throws(InteractionNotFound)
           .Handle(async (ctx, request) =>
            {
                var interaction = interactionStore.TryPeek(request.InteractionId)
                               ?? throw InteractionNotFound.Raise();

                await Pusher(ctx).PushDeferredAsync(request.InteractionId, interaction.UserId);
            });

        group.Post<ModalRequest, ModalResponse>("/Modal")
           .Summary("Show a modal dialog to the user who triggered the interaction.")
           .Throws(InteractionNotFound)
           .Throws(ValidationError)
           .Handle(async (ctx, request) =>
            {
                var interaction = interactionStore.TryConsume(request.InteractionId)
                               ?? throw InteractionNotFound.Raise();

                // Validation used to escape as a 500 while the documentation promised a 400. The
                // declaration and the behaviour are one thing now, so it is the 400 that was
                // promised.
                try
                {
                    request.Modal.Validate();
                }
                catch (ArgumentException e)
                {
                    throw ValidationError.Raise(e.Message);
                }

                var modalInteractionId = ArgonId.New();
                interactionStore.Register(
                    modalInteractionId,
                    interaction.UserId,
                    interaction.ChannelId,
                    interaction.SpaceId,
                    interaction.BotAppId);

                await Pusher(ctx).PushShowModalAsync(modalInteractionId, interaction.UserId, request.Modal);

                return new ModalResponse(modalInteractionId);
            });
    }

    private static InteractionResponsePusher Pusher(HttpContext ctx)
        => ctx.RequestServices.GetRequiredService<InteractionResponsePusher>();
}
