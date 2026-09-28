namespace Argon.Grains;

public partial class ChannelGrain
{
    public async Task<BotMessageSent> SendBotMessage(BotMessageSend request)
    {
        var entities = (request.Entities ?? []).Where(e => e is not MessageEntityAttachment).ToList();
        var fileIds  = (request.Attachments ?? []).Distinct().ToList();
        var files    = new List<MessageEntityAttachment>(fileIds.Count);

        if (fileIds.Count > 0)
        {
            if (fileIds.Count > BotFileLimits.MaxAttachmentsPerMessage)
                return Refused(SendMessageError.TOO_MANY_ATTACHMENTS);

            // Before the uploads are looked up, so a bot that may not attach learns nothing about them.
            if (!await entitlementChecker.HasChannelAccessAsync(SpaceId, this.GetPrimaryKey(), this.GetUserId(), ArgonEntitlement.AttachFiles))
                return Refused(SendMessageError.NO_ATTACH_PERMISSION);

            var uploads = await GrainFactory.GetGrain<IBotFilesGrain>(this.GetUserId()).DescribeAttachmentsAsync(fileIds);
            var missing = fileIds.Where(id => !uploads.ContainsKey(id)).ToList();

            if (missing.Count > 0)
                return new BotMessageSent(SendMessageError.INVALID_DATA, 0, [], missing);

            files.AddRange(fileIds.Select(id => uploads[id]).Select(f => new MessageEntityAttachment(EntityType.Attachment, 0, 0, 1,
                f.FileId, f.FileName, f.Size, f.ContentType, f.Width, f.Height, null, null)));
            entities.AddRange(files);
        }

        var (error, messageId) = await SendMessage(request.Text, entities, request.RandomId, request.ReplyTo, request.Controls);
        if (error != SendMessageError.NONE)
            return Refused(error);

        if (fileIds.Count > 0)
            _ = RetainAttachmentsAsync(fileIds);

        return new BotMessageSent(SendMessageError.NONE, messageId,
            files.Select(f => f with { downloadUrl = s3.GetFileDownloadUrl(f.fileId) }).ToList(), []);

        static BotMessageSent Refused(SendMessageError error) => new(error, 0, [], []);
    }

    /// <summary>Off the send: the uploads have a day before they could be collected.</summary>
    private async Task RetainAttachmentsAsync(List<Guid> fileIds)
    {
        try
        {
            await GrainFactory.GetGrain<IBotFilesGrain>(this.GetUserId()).RetainAttachmentsAsync(fileIds);
        }
        catch (Exception e)
        {
            logger.LogError(e, "bot attachments {FileIds} in channel {ChannelId} were not retained", fileIds, this.GetPrimaryKey());
        }
    }
}
