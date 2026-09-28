namespace Argon.Api.BotApi.Interfaces;

using Argon.Features.BotApi;

[BotInterface("IMessages", 1)]
[BotDescription("Send messages with text and files, and retrieve message history from channels.")]
public sealed class MessagesV1(IGrainFactory grains) : IBotInterface
{
    public sealed record SendMessageRequest(
        Guid                  ChannelId,
        string                Text,
        long                  RandomId,
        long?                 ReplyTo     = null,
        List<IMessageEntity>? Entities    = null,
        List<ControlRowV1>?   Controls    = null,
        List<BotInputFile>?   Attachments = null);

    public sealed record SendMessageResponse(
        long                   MessageId,
        List<BotAttachmentV1>? Attachments = null);

    public sealed record MessageHistoryQuery(
        Guid  ChannelId,
        long? From  = null,
        int?  Limit = null);

    public sealed record MessageDto(
        long                   MessageId,
        Guid                   ChannelId,
        Guid                   SpaceId,
        string                 Text,
        Guid                   CreatorId,
        DateTimeOffset         CreatedAt,
        long?                  ReplyTo,
        List<IMessageEntity>   Entities,
        List<ControlRowV1>?    Controls    = null,
        List<ReactionDto>?     Reactions   = null,
        List<BotAttachmentV1>? Attachments = null);

    public sealed record ReactionDto(
        string     Emoji,
        int        Count,
        List<Guid> UserIds,
        Guid?      CustomEmojiId = null);

    public sealed record MessageHistoryResponse(
        List<MessageDto> Messages);

    public void MapRoutes(RouteGroupBuilder group)
    {
        group.AddEndpointFilter<BotOrleansPropagationFilter>();
        group.RequireRateLimiting("Bot_IMessages");

        group.Post<SendMessageRequest, SendMessageResponse>("/Send")
           .Summary("Sends a message to a channel. Include a unique randomId for deduplication; reply to another message via replyTo. attachments (at most 10, AttachFiles) are fileIds from IFiles/Upload with purpose attachment, or, in a multipart request with the same fields, parts, attach://<part> references or parts named attachments; the parts of one request share its 10 MiB limit. The response describes the attachments. Bots cannot post in announcement channels.")
           .Permission(ArgonEntitlement.SendMessages)
           .FromBodyOrForm(BotFileLimits.MaxAttachmentRequestBytes)
           .Throws(BotSendErrors.CannotSend)
           .Throws(BotSendErrors.InvalidMessage)
           .Throws(BotSendErrors.SlowMode)
           .Throws(BotSendErrors.NoAttachPermission)
           .Throws(BotSendErrors.UploadNotFound)
           .Throws(BotSendErrors.EmptyAttachment)
           .Handle(async (ctx, request) =>
            {
                var files = request.Attachments ?? [];
                if (files.Count > BotFileLimits.MaxAttachmentsPerMessage)
                    throw BotSendErrors.Raise(SendMessageError.TOO_MANY_ATTACHMENTS);

                var fileIds = new List<Guid>(files.Count);
                foreach (var file in files)
                    fileIds.Add(await FileIdOfAsync(ctx, file));

                var sent = await grains.GetGrain<IChannelGrain>(request.ChannelId).SendBotMessage(new BotMessageSend(
                    request.Text,
                    request.Entities ?? [],
                    request.RandomId,
                    request.ReplyTo,
                    request.Controls,
                    fileIds));

                if (sent.MissingUploads is [var missing, ..])
                    throw BotSendErrors.UploadNotFound.Raise($"{missing} is no attachment upload of this bot, or it is over 24 hours old.");
                if (sent.Error is SendMessageError.NO_ATTACH_PERMISSION)
                    throw BotSendErrors.NoAttachPermission.Raise();
                if (sent.Error is not SendMessageError.NONE)
                    throw BotSendErrors.Raise(sent.Error);

                return new SendMessageResponse(sent.MessageId, sent.Attachments.Select(BotEventMapper.FromAttachment).ToList());
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
                        m.Reactions?.Select(r => new ReactionDto(r.Emoji, r.UserIds.Count, r.UserIds.Take(3).ToList(), r.CustomEmojiId)).ToList(),
                        AttachmentsOf(m.Entities))).ToList());
            });
    }

    /// <summary>A file given by fileId as it is; a part is uploaded first, as an attachment that is not kept for reuse.</summary>
    private async Task<Guid> FileIdOfAsync(HttpContext ctx, BotInputFile file)
    {
        if (file.FileId is { } fileId)
            return fileId;

        if (file.Data is not { } data)
            throw BotErrors.InvalidRequest.Raise($"{file} names a part; send the request as multipart/form-data.");

        var uploaded = await grains.GetGrain<IBotFilesGrain>(ctx.GetBotAsUserId())
           .UploadAsync(BotFilePurpose.Attachment, data, file.FileName, file.ContentType, inline: true);

        return uploaded.Error switch
        {
            ExpressionError.NONE           => uploaded.File!.FileId,
            ExpressionError.TOO_LARGE      => throw BotErrors.TooLarge.Raise($"{file.FileName ?? "an attachment"} is over 10 MiB."),
            ExpressionError.INVALID_FORMAT => throw BotSendErrors.EmptyAttachment.Raise(),
            _                              => throw new InvalidOperationException($"attachment upload refused: {uploaded.Error}")
        };
    }

    private static List<BotAttachmentV1>? AttachmentsOf(List<IMessageEntity>? entities)
        => entities?.OfType<MessageEntityAttachment>().Select(BotEventMapper.FromAttachment).ToList() is { Count: > 0 } found ? found : null;
}
