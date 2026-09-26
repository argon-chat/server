namespace Argon.Api.BotApi.Interfaces;

using Argon.Features.BotApi;

/// <summary>A refused bot message, shared by IMessages/Send and IInteractions/Reply.</summary>
internal static class BotSendErrors
{
    public static readonly BotError CannotSend = new(403, "cannot_send",
        "Bot may not post this message in this channel.");

    public static readonly BotError InvalidMessage = new(400, "validation_error",
        "The message is not valid for this channel.");

    public static readonly BotError SlowMode = new(429, "slow_mode",
        "The channel is holding messages back; retry later.");

    public static BotApiException Raise(SendMessageError error) => error switch
    {
        SendMessageError.NO_PERMISSION        => CannotSend.Raise("Bot does not have SendMessages permission in this channel."),
        SendMessageError.NO_ATTACH_PERMISSION => CannotSend.Raise("Bot does not have AttachFiles permission in this channel."),
        SendMessageError.BOTS_NOT_ALLOWED     => CannotSend.Raise("Bots cannot post in announcement channels."),
        SendMessageError.NOT_TEXT_CHANNEL     => InvalidMessage.Raise("The channel is not a text channel."),
        SendMessageError.TEXT_TOO_LONG        => InvalidMessage.Raise("The message text is too long."),
        SendMessageError.TOO_MANY_ATTACHMENTS => InvalidMessage.Raise("At most 10 attachments per message."),
        SendMessageError.INVALID_DATA         => InvalidMessage.Raise("The message controls are invalid."),
        SendMessageError.SLOW_MODE            => SlowMode.Raise("Slow mode is active in this channel."),
        SendMessageError.CHANNEL_CAP          => SlowMode.Raise("The channel is receiving too many messages."),
        _                                     => throw new InvalidOperationException($"message refused: {error}")
    };
}
