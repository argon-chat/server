namespace Argon.Services.Ion;

using Argon.Api.Grains.Interfaces;

internal static class PrepareUploads
{
    /// <summary>The grain's answer on the wire: a ticket, the copy made instead, or the refusal.</summary>
    public static IPrepareUploadResult ToResult(Either<PreparedUpload, PrepareUploadError> result)
    {
        if (!result.IsSuccess)
            return new FailedPrepareUpload(result.Error);

        var prepared = result.Value;

        if (prepared.Existing is { } file)
            return new AlreadyStored(new AttachmentInfo(file.FileId, file.FileName ?? "", file.FileSize, file.ContentType ?? "", file.DownloadUrl));

        var t = prepared.Ticket!;
        return new UploadRequired(t.BlobId, t.Url, UploadHelpers.ToFormFields(t.Fields), t.TtlSeconds);
    }
}
