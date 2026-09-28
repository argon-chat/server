namespace Argon.Core.Grains.Interfaces;

using Argon.Api.Grains.Interfaces;
using Argon.Grains.Interfaces;
using Entities.Data;

[Alias(nameof(IUserChatGrain))]
public interface IUserChatGrain : IGrainWithGuidKey
{
    [Alias(nameof(GetRecentChatsAsync))]
    Task<List<UserChat>> GetRecentChatsAsync(int limit, int offset, CancellationToken ct = default);

    [Alias(nameof(PinChatAsync))]
    Task PinChatAsync(Guid peerId, CancellationToken ct = default);

    [Alias(nameof(UnpinChatAsync))]
    Task UnpinChatAsync(Guid peerId, CancellationToken ct = default);

    [Alias(nameof(MarkChatReadAsync))]
    Task MarkChatReadAsync(Guid peerId, CancellationToken ct = default);

    /// <summary>
    /// Hides the conversation on the caller's side only: the row is archived, nothing is destroyed,
    /// and the next message either side sends brings it back.
    /// </summary>
    [Alias(nameof(DeleteChatAsync))]
    Task DeleteChatAsync(Guid peerId, CancellationToken ct = default);

    /// <summary>
    /// A ticket to put a file into the chat with <paramref name="peerId"/>. Nothing to be entitled
    /// to in a direct chat; a block from the other side is the one thing that refuses it.
    /// </summary>
    [Alias(nameof(BeginUploadAttachmentAsync))]
    ValueTask<Either<UploadTicket, UploadFileError>> BeginUploadAttachmentAsync(Guid peerId, CancellationToken ct = default);

    [Alias(nameof(CompleteUploadAttachmentAsync)), ResponseTimeout("00:01:00")]
    ValueTask<AttachmentInfo> CompleteUploadAttachmentAsync(Guid blobId, CancellationToken ct = default);

    /// <summary>
    /// A copy of an existing file into the chat with <paramref name="peerId"/>, without the bytes
    /// moving. A block from the other side refuses it, as does a source the caller may not read.
    /// </summary>
    [Alias(nameof(AttachExistingFileAsync))]
    ValueTask<Either<AttachmentInfo, AttachExistingFileError>> AttachExistingFileAsync(Guid peerId, Guid sourceFileId, string? fileName,
        CancellationToken ct = default);

    /// <summary>The channel grain's PrepareUploadAttachment, for the chat with <paramref name="peerId"/>.</summary>
    [Alias(nameof(PrepareUploadAttachmentAsync))]
    ValueTask<Either<PreparedUpload, PrepareUploadError>> PrepareUploadAttachmentAsync(Guid peerId, byte[] sha256, long size, string contentType,
        string fileName, CancellationToken ct = default);

    [Alias(nameof(UpdateChatForAsync))]
    Task UpdateChatForAsync(Guid userId, Guid peerId, string? previewText, DateTimeOffset timestamp, CancellationToken ct = default);

    [Alias(nameof(SendDirectMessageAsync))]
    Task<long> SendDirectMessageAsync(Guid receiverId, string text, List<IMessageEntity> entities, long randomId, long? replyTo, CancellationToken ct = default);

    [Alias(nameof(QueryDirectMessagesAsync))]
    Task<List<DirectMessage>> QueryDirectMessagesAsync(Guid peerId, long? from, int limit, CancellationToken ct = default);
}