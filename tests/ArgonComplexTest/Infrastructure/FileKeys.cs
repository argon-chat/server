namespace ArgonComplexTest.Infrastructure;

using Argon.Entities;
using Argon.Features.Storage;
using Microsoft.EntityFrameworkCore;

/// <summary>
/// A file's object, for tests: the key lives on the blob the file names, not on the file.
/// </summary>
public static class FileKeys
{
    /// <summary>The key of the object a file names, soft-deleted rows included.</summary>
    public static Task<string> KeyOfAsync(this ApplicationDbContext db, Guid fileId, CancellationToken ct = default)
        => (from f in db.Files.IgnoreQueryFilters()
            join b in db.Blobs.IgnoreQueryFilters() on f.BlobId equals b.Id
            where f.Id == fileId
            select b.S3Key).SingleAsync(ct);

    /// <summary>Adds a file together with the blob it names, the way finalize leaves them; the file's <c>BlobId</c> is set.</summary>
    public static FileEntity Seed(ApplicationDbContext db, FileEntity file, string key, long? links = null)
    {
        var now = DateTimeOffset.UtcNow;

        var blob = new BlobEntity
        {
            Id          = Guid.CreateVersion7(),
            S3Key       = key,
            Size        = file.FileSize,
            ContentType = BlobHashes.NormalizeContentType(file.ContentType),
            Links       = links ?? (file.Finalized ? 1 : 0),
            Dedupable   = file.Purpose.IsDedupable(),
            CreatedAt   = file.CreatedAt == default ? now : file.CreatedAt,
            UpdatedAt   = now
        };

        file.BlobId = blob.Id;
        db.Blobs.Add(blob);
        db.Files.Add(file);
        return file;
    }
}
