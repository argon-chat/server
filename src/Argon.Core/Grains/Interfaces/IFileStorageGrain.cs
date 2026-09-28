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
    Task<FileInfoResponse> LinkAsync(Guid sourceFileId, FileUploadRequest target, string? fileName, CancellationToken ct = default);

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
}
