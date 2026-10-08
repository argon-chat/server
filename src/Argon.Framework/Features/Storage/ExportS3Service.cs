namespace Argon.Features.Storage;

using Genbox.SimpleS3.Core.Abstracts.Clients;
using Genbox.SimpleS3.Core.Network.Requests.Objects;
using Genbox.SimpleS3.Core.Network.Responses.Objects;

public interface IExportS3Service
{
    /// <summary>Whether this deployment has an export bucket at all.</summary>
    /// <remarks>
    /// <para>"No export store" is a supported deployment shape, not a broken one:
    /// <c>StorageOptions.ExportBucketName</c> ships as <c>""</c> and
    /// <c>ObjectStorageHealthCheck</c> deliberately filters an empty export bucket out of its probe,
    /// so a self-hosted instance that never set it is healthy and simply has no data-export feature.
    /// Every call below would name a bucket called <c>""</c> instead, which the store answers with an
    /// error — indistinguishable, to a caller, from a bucket that is failing.</para>
    ///
    /// <para>That distinction is the whole point of this flag, and it was worth an account. Since
    /// finding R16 <see cref="ListObjectsAsync"/> throws on a non-success page — correct for a bucket
    /// that is failing — so <c>AccountDeletionGrain.PurgeExportArchivesAsync</c>, which is step 2 of
    /// ten and is deliberately allowed to fail the attempt, threw on every attempt on such an
    /// instance. Three attempts later the erasure was disarmed for good and nothing on the instance
    /// said the feature was inoperable. A caller asks this first and skips the work; a caller that
    /// gets past it is entitled to treat an error as a fault.</para>
    ///
    /// <para><c>UserDataExportGrain.RequestExportAsync</c> is the other reader this is owed to:
    /// <c>ExportRequestError.NotConfigured</c> exists in the enum and nothing produces it, so asking
    /// for an archive on such an instance fails as an internal error rather than as "this deployment
    /// does not do that". That belongs to the export path and is left to it.</para>
    /// </remarks>
    bool IsConfigured { get; }

    Task<bool> PutObjectAsync(string objectKey, Stream content, string? contentType = null, CancellationToken ct = default);
    Task<Stream?> GetObjectStreamAsync(string objectKey, CancellationToken ct = default);
    Task<bool> DeleteObjectAsync(string objectKey, CancellationToken ct = default);
    Task<List<string>> ListObjectsAsync(string prefix, CancellationToken ct = default);
    Task DeletePrefixAsync(string prefix, CancellationToken ct = default);
    string GeneratePresignedGetUrl(string objectKey, int expirationSeconds = 172800);
}

public class ExportS3Service(
    IS3ClientPool clientPool,
    IOptions<StorageOptions> options,
    S3PresignedUrlGenerator presignedUrlGenerator) : IExportS3Service
{
    private readonly StorageOptions _opts = options.Value;

    private string BucketName => _opts.ExportBucketName;

    /// <inheritdoc cref="IExportS3Service.IsConfigured"/>
    /// <remarks>
    /// Whitespace counts as unset for the same reason <c>ObjectStorageHealthCheck</c> treats it that
    /// way: a bucket name is copied out of a config file by hand, and " " naming nothing is the same
    /// deployment as "" naming nothing.
    /// </remarks>
    public bool IsConfigured => !string.IsNullOrWhiteSpace(BucketName);

    public async Task<bool> PutObjectAsync(string objectKey, Stream content, string? contentType = null, CancellationToken ct = default)
    {
        var client = clientPool.GetClient();
        var response = await client.PutObjectAsync(BucketName, objectKey, content, null, ct);
        return response.IsSuccess;
    }

    public async Task<Stream?> GetObjectStreamAsync(string objectKey, CancellationToken ct = default)
    {
        var client = clientPool.GetClient();
        var response = await client.GetObjectAsync(BucketName, objectKey, null, ct);

        if (!response.IsSuccess) return null;

        var ms = new MemoryStream(response.ContentLength > 0 ? (int)response.ContentLength : 4096);
        await response.Content.CopyToAsync(ms, ct);
        ms.Position = 0;
        return ms;
    }

    public async Task<bool> DeleteObjectAsync(string objectKey, CancellationToken ct = default)
    {
        var client = clientPool.GetClient();
        var response = await client.DeleteObjectAsync(BucketName, objectKey, null, ct);
        return response.IsSuccess;
    }

    /// <summary>
    /// Every key under <paramref name="prefix"/>, following the store's continuation tokens.
    /// </summary>
    /// <remarks>
    /// A single ListObjectsV2 call answers with at most a thousand keys and sets <c>IsTruncated</c>;
    /// the first version of this method read that one page and returned it as if it were the whole
    /// prefix. Both callers treat the result as complete — <c>DeletePrefixAsync</c> deletes what it
    /// lists, and the export's assembly step zips what it lists — so a truncated listing meant
    /// intermediate JSON left in the bucket forever and an archive missing whatever the second page
    /// held. An export writes one object per category plus one per conversation and per channel, so
    /// a heavy account passes a thousand keys on its own. Pinned by
    /// <c>DataExportArchiveTests.A_failed_export_does_not_leave_its_intermediate_files_behind</c>
    /// through the deletion path (defect X3, scout H9).
    /// </remarks>
    /// <remarks>
    /// A page that comes back unsuccessful throws rather than ending the loop early (finding R16).
    /// Returning what had been collected so far moved the same defect from truncation to failure and
    /// made it invisible: neither caller can tell a short list from a finished one, so a throttle on
    /// page two of four meant assembly zipped a thousand of fourteen hundred keys, uploaded that as
    /// the person's archive, and then deleted all fourteen hundred — a silently incomplete Art. 15
    /// response with nothing anywhere recording what was left out. Throwing lands in the export
    /// tick's catch, which fails the export honestly and tells the person; the callers that clean up
    /// best-effort already wrap this.
    /// </remarks>
    public async Task<List<string>> ListObjectsAsync(string prefix, CancellationToken ct = default)
    {
        var client = clientPool.GetClient();
        var keys   = new List<string>();

        string? continuation = null;
        var     page         = 0;

        do
        {
            var token    = continuation;
            var response = await client.ListObjectsAsync(BucketName, req =>
            {
                req.Prefix            = prefix;
                req.ContinuationToken = token;
            }, ct);

            page++;

            if (!response.IsSuccess)
                throw new InvalidOperationException(
                    $"Listing '{prefix}' failed on page {page} with status {response.StatusCode}; " +
                    $"{keys.Count} keys had been read and a partial listing is not a listing");

            if (response.Objects != null)
            {
                foreach (var obj in response.Objects)
                    keys.Add(obj.ObjectKey);
            }

            continuation = response.IsTruncated ? response.NextContinuationToken : null;
        }
        while (!string.IsNullOrEmpty(continuation));

        return keys;
    }

    /// <summary>Deletes everything under <paramref name="prefix"/>, or says which keys survived.</summary>
    /// <remarks>
    /// Every delete response is read (finding R16). The callers use this to make a promise — the
    /// cancelled export's working files are gone, the failed export's are gone, the erased account's
    /// archives are gone — and a delete that answers 503 while the loop ignores the answer leaves
    /// <c>profile.json</c> and <c>devices.json</c> in the bucket under a claim that they are not
    /// there. The whole prefix is still attempted before the throw, so one unlucky key does not
    /// strand the rest, and the message names what is left for whoever reads the log.
    /// </remarks>
    public async Task DeletePrefixAsync(string prefix, CancellationToken ct = default)
    {
        var keys   = await ListObjectsAsync(prefix, ct);
        var client = clientPool.GetClient();
        var failed = new List<string>();

        foreach (var key in keys)
        {
            var response = await client.DeleteObjectAsync(BucketName, key, null, ct);

            if (!response.IsSuccess)
                failed.Add(key);
        }

        if (failed.Count > 0)
            throw new InvalidOperationException(
                $"{failed.Count} of {keys.Count} objects under '{prefix}' were not deleted: {string.Join(", ", failed)}");
    }

    public string GeneratePresignedGetUrl(string objectKey, int expirationSeconds = 172800)
        => presignedUrlGenerator.GeneratePresignedGet(BucketName, objectKey, expirationSeconds);
}
