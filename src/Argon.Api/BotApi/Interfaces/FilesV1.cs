namespace Argon.Api.BotApi.Interfaces;

using Argon.Features.BotApi;

[BotInterface("IFiles", 1)]
[BotDescription("Upload files once and use them by fileId: stickers and emoji for IExpressions/AddItem, attachments for IMessages/Send. An upload is kept for 24 hours; at most 100 unused uploads per bot.")]
public sealed class FilesV1(IGrainFactory grains) : IBotInterface
{
    public sealed record UploadFileRequest(
        [property: BotPartOnly] BotInputFile File,
        BotUploadPurpose Purpose);

    public sealed record GetFileQuery(
        Guid FileId);

    public void MapRoutes(RouteGroupBuilder group)
    {
        group.AddEndpointFilter<BotOrleansPropagationFilter>();
        group.RequireRateLimiting("Bot_IFiles");

        group.Post<UploadFileRequest, BotFileV1>("/Upload")
           .Summary("Uploads a file (multipart: file, purpose) to be taken by fileId, any number of times, within 24 hours. sticker and emoji: PNG, WEBP, TGS, Lottie JSON or WEBM, for IExpressions/AddItem. attachment: any type up to 10 MiB, keeping the part's file name and content type, for IMessages/Send.")
           .FromForm(BotFileLimits.MaxAttachmentRequestBytes)
           .Throws(ExpressionBotErrors.InvalidFormat)
           .Throws(ExpressionBotErrors.QuotaExceeded)
           .Handle(async (ctx, request) =>
            {
                if (request.File.Data is not { } data)
                    throw BotErrors.InvalidRequest.Raise("file has to be a part of the request.");

                var result = await grains.GetGrain<IBotFilesGrain>(ctx.GetBotAsUserId())
                   .UploadAsync((BotFilePurpose)(int)request.Purpose, data, request.File.FileName, request.File.ContentType);

                if (result.Error is not ExpressionError.NONE)
                    throw ExpressionBotErrors.Raise(ctx, result.Error);

                return Describe(result.File!);
            });

        group.Get<GetFileQuery, BotFileV1>("/Get")
           .Summary("Describes a file the bot uploaded or owns, or any sticker or emoji file: its size, type and a download URL.")
           .Throws(ExpressionBotErrors.NotFound)
           .Handle(async (ctx, query) =>
                Describe(await grains.GetGrain<IBotFilesGrain>(ctx.GetBotAsUserId()).GetAsync(query.FileId)
                      ?? throw ExpressionBotErrors.NotFound.Raise()));
    }

    private static BotFileV1 Describe(BotFileDescription file) => new(file.FileId, file.Size, file.ContentType, file.Url, file.FileName);
}
