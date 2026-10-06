namespace Argon.Api.Grains.Interfaces;

using Argon.Features.Storage;

[GenerateSerializer]
public record FileUploadRequest(
    [property: Id(0)] FilePurpose Purpose,
    [property: Id(1)] string ContentType,
    [property: Id(2)] long FileSize,
    [property: Id(3)] Guid? SpaceId = null,
    [property: Id(4)] Guid? ChannelId = null,
    [property: Id(5)] string? FileName = null,
    [property: Id(6)] byte[]? ClaimedSha256 = null);

[GenerateSerializer]
public record FileUploadResponse(
    [property: Id(0)] Guid BlobId,
    [property: Id(1)] Guid FileId,
    [property: Id(2)] string Url,
    [property: Id(3)] Dictionary<string, string> Fields,
    [property: Id(4)] int TtlSeconds);

[GenerateSerializer]
public record FileInfoResponse(
    [property: Id(0)] Guid FileId,
    [property: Id(1)] string? FileName,
    [property: Id(2)] long FileSize,
    [property: Id(3)] string? ContentType,
    [property: Id(4)] FilePurpose Purpose,
    [property: Id(5)] string DownloadUrl,
    [property: Id(6)] string S3Key);

/// <summary>A video the caller's client prepared, for a channel (<see cref="SpaceId"/> set) or a direct chat (conversation as <see cref="ChannelId"/>).</summary>
[GenerateSerializer]
public record VideoUploadRequest(
    [property: Id(0)] Guid?                  SpaceId,
    [property: Id(1)] Guid?                  ChannelId,
    [property: Id(2)] FilePurpose            Purpose,
    [property: Id(3)] VideoUploadDeclaration Declaration);

/// <summary>What a prepared upload came to: a ticket to send the bytes on, or a copy of a file the account could already see.</summary>
[GenerateSerializer]
public record PreparedUpload(
    [property: Id(0)] FileUploadResponse? Ticket,
    [property: Id(1)] FileInfoResponse? Existing);

[Alias(nameof(IFileStorageGrain))]
public interface IFileStorageGrain : IGrainWithGuidKey
{
    /// <summary>
    ///     Request a presigned PUT URL for uploading a file.
    ///     Grain key = userId.
    /// </summary>
    [Alias(nameof(RequestUploadAsync))]
    Task<FileUploadResponse> RequestUploadAsync(FileUploadRequest request, CancellationToken ct = default);

    /// <summary>
    ///     A request with the bytes described up front (<see cref="FileUploadRequest.ClaimedSha256"/>,
    ///     size, type, name). When the account can already see a file with these bytes — its own, by the
    ///     hash it claimed for it, or one posted in a channel of the target's space it can read, by a hash
    ///     the server computed — a copy is linked instead of a ticket. An over-limit size is refused
    ///     before anything is signed. The caller has checked it may post in the target.
    /// </summary>
    [Alias(nameof(PrepareUploadAsync))]
    Task<Either<PreparedUpload, PrepareUploadError>> PrepareUploadAsync(FileUploadRequest request, CancellationToken ct = default);

    /// <summary>
    ///     Finalize upload after client has uploaded to S3. Validates via HEAD.
    /// </summary>
    [Alias(nameof(FinalizeUploadAsync)), ResponseTimeout("00:01:00")]
    Task<FileInfoResponse> FinalizeUploadAsync(Guid blobId, CancellationToken ct = default);

    /// <summary>
    ///     Stores bytes the server already holds: request, put and finalize in one call. With
    ///     <paramref name="unclaimedFor"/> the file starts without a reference and is collectable that long from now.
    /// </summary>
    [Alias(nameof(StoreAsync)), ResponseTimeout("00:01:00")]
    Task<FileInfoResponse> StoreAsync(FileUploadRequest request, byte[] data, string? cacheControl, TimeSpan? unclaimedFor,
        CancellationToken ct = default);

    /// <summary>
    ///     A new file of the caller's over the object of an existing one: a copy without bytes.
    /// </summary>
    /// <remarks>
    ///     The caller has checked that the account may read <paramref name="sourceFileId"/> — its
    ///     owner, or somebody who can view the channel it was posted in — the way the grain that owns
    ///     the target checks it can post there. This grain holds the copy to the target's size limit
    ///     and content types, and refuses a source whose object is not shareable.
    /// </remarks>
    [Alias(nameof(LinkAsync))]
    Task<Either<FileInfoResponse, AttachExistingFileError>> LinkAsync(Guid sourceFileId, FileUploadRequest target, string? fileName,
        CancellationToken ct = default);

    /// <summary>
    ///     Increment reference count for a file the caller owns (e.g. attached to a message).
    /// </summary>
    /// <remarks>Owner-scoped, exactly as <see cref="DecrementRefAsync"/> is — see its remarks.</remarks>
    [Alias(nameof(IncrementRefAsync))]
    Task IncrementRefAsync(Guid fileId, CancellationToken ct = default);

    /// <summary>
    ///     Decrement reference count for a file the caller owns (e.g. message deleted).
    /// </summary>
    /// <remarks>
    ///     <b>Both counters are scoped to the file's owner, and the grain key is who that is.</b>
    ///     <c>FileStorageController</c> puts <c>POST /api/files/{fileId}/increment|decrement</c> in
    ///     front of these with the caller's id as the grain key and the file id straight off the
    ///     route, so without the scope any authenticated caller could take any file to zero
    ///     references and have <c>FileGcService</c> delete it (defect R1). A call naming a file the
    ///     caller does not own does nothing and is logged; it is not an error, because the honest
    ///     answer to "release this" from somebody who holds nothing is that nothing happened.
    /// </remarks>
    [Alias(nameof(DecrementRefAsync))]
    Task DecrementRefAsync(Guid fileId, CancellationToken ct = default);

    /// <summary>
    ///     Get file metadata and download URL.
    /// </summary>
    [Alias(nameof(GetFileInfoAsync))]
    Task<FileInfoResponse?> GetFileInfoAsync(Guid fileId, CancellationToken ct = default);

    /// <summary>
    ///     Get presigned GET URL for a private file.
    /// </summary>
    [Alias(nameof(GetDownloadUrlAsync))]
    Task<string?> GetDownloadUrlAsync(Guid fileId, CancellationToken ct = default);

    /// <summary>
    ///     A ticket for the video the declaration describes, or a copy when the account can already see
    ///     these bytes. Over-limit, over-long, non-MP4 and foreign posters are refused before anything is
    ///     signed. The caller has checked it may post in the target.
    /// </summary>
    /// <remarks>
    ///     Refusals: <c>TOO_LARGE</c> over the tier's limit, <c>TOO_LONG</c> over the duration cap,
    ///     <c>CONTENT_TYPE_REJECTED</c> for anything but <c>video/mp4</c> or a codec other than H.264,
    ///     <c>POSTER_REJECTED</c> for a poster or storyboard that is not the caller's finalized image in the
    ///     same chat, <c>DECLARATION_MISMATCH</c> for impossible values, and <c>NOT_AUTHORIZED</c> once the
    ///     account holds <see cref="FileLimitsOptions.VideoMaxOpenTickets"/> open tickets.
    /// </remarks>
    [Alias(nameof(PrepareVideoUploadAsync)), ResponseTimeout("00:02:00")]
    Task<IVideoUploadResult> PrepareVideoUploadAsync(VideoUploadRequest request, CancellationToken ct = default);

    /// <summary>
    ///     Closes a video ticket of the caller's: completes the multipart upload, reads the MP4 header,
    ///     checks it and keeps the media record. Called again for a finished ticket it answers the same.
    /// </summary>
    /// <remarks>
    ///     Refusals: <c>DECLARATION_MISMATCH</c> for a parts list S3 does not take (the ticket and its
    ///     upload stay for a retry) or a header that disagrees with the declaration, <c>NOT_STREAMABLE</c>
    ///     without <c>moov</c> first within <see cref="FileLimitsOptions.VideoProbeBytes"/> or without a
    ///     duration, <c>TOO_LONG</c>, <c>CONTENT_TYPE_REJECTED</c> for a codec other than H.264, and
    ///     <c>TICKET_EXPIRED</c>. A refused header costs the object, not the ticket.
    /// </remarks>
    [Alias(nameof(CompleteVideoUploadAsync)), ResponseTimeout("00:02:00")]
    Task<IVideoUploadResult> CompleteVideoUploadAsync(Guid ticketId, UploadedPart[] parts, CancellationToken ct = default);

    /// <summary>Drops a video ticket of the caller's, its parts and its object.</summary>
    [Alias(nameof(AbortVideoUploadAsync))]
    Task AbortVideoUploadAsync(Guid ticketId, CancellationToken ct = default);

    /// <summary>
    ///     The caller's size limits for an attachment and a video in a channel of <paramref name="spaceId"/>,
    ///     or in a direct chat when it is null (base and Ultima only), and the video duration cap.
    /// </summary>
    [Alias(nameof(GetUploadLimitsAsync))]
    Task<UploadLimits> GetUploadLimitsAsync(Guid? spaceId, CancellationToken ct = default);

    /// <summary>The media record of a finalized video file, or null.</summary>
    [Alias(nameof(GetVideoInfoAsync))]
    Task<VideoInfo?> GetVideoInfoAsync(Guid fileId, CancellationToken ct = default);

    /// <summary>The caller's videos among <paramref name="fileIds"/> that were uploaded into <paramref name="scopeId"/> (a channel or a conversation).</summary>
    [Alias(nameof(GetSendableVideosAsync))]
    Task<Dictionary<Guid, VideoInfo>> GetSendableVideosAsync(Guid scopeId, List<Guid> fileIds, CancellationToken ct = default);
}
