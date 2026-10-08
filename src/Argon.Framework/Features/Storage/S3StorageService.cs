namespace Argon.Features.Storage;

using System.Diagnostics;
using Genbox.SimpleS3.Core.Abstracts.Clients;
using Genbox.SimpleS3.Core.Network.Requests.S3Types;

public interface IS3StorageService
{
    Task<bool> FileExistsAsync(string objectKey, CancellationToken ct = default);
    Task<S3FileMetadata?> HeadFileAsync(string objectKey, CancellationToken ct = default);
    Task<bool> DeleteFileAsync(string objectKey, CancellationToken ct = default);
    Task<Stream?> GetObjectStreamAsync(string objectKey, CancellationToken ct = default);
    /// <summary>The object's body as the store sends it, not buffered; for reading once, front to back.</summary>
    Task<Stream?> OpenReadAsync(string objectKey, CancellationToken ct = default);
    /// <summary>Bytes <paramref name="from"/>..<paramref name="toInclusive"/> of the object, not buffered.</summary>
    Task<Stream?> OpenReadRangeAsync(string objectKey, long from, long toInclusive, CancellationToken ct = default);
    /// <summary>Starts a multipart upload; the upload id, or null when the store refused.</summary>
    Task<string?> CreateMultipartUploadAsync(string objectKey, string contentType, string? cacheControl, CancellationToken ct = default);
    Task<bool> CompleteMultipartUploadAsync(string objectKey, string uploadId, IReadOnlyList<(int PartNumber, string ETag)> parts,
        CancellationToken ct = default);
    Task<bool> AbortMultipartUploadAsync(string objectKey, string uploadId, CancellationToken ct = default);
    Task<bool> PutObjectAsync(string objectKey, Stream content, string? contentType = null, string? cacheControl = null, CancellationToken ct = default);
    // Region-agnostic URLs. Region is resolved later, per request, by the 302 endpoint in
    // CdnRedirectFeature — never baked in here.
    // GetFileDownloadUrl: by fileId (avatars/attachments/banners backed by a file record).
    // GetDownloadUrl:     by raw S3 key (keyless assets: cached GIFs, flat-keyed avatar in exports).
    string GetFileDownloadUrl(Guid fileId);
    string GetDownloadUrl(string objectKey);
}

public class S3FileMetadata
{
    public long   ContentLength { get; init; }
    public string? ContentType  { get; init; }
    public string? ETag         { get; init; }
    public string? CacheControl { get; init; }
}

public class S3StorageService(IS3ClientPool clientPool, IOptions<StorageOptions> options) : IS3StorageService
{
    private readonly StorageOptions _opts = options.Value;

    public async Task<bool> FileExistsAsync(string objectKey, CancellationToken ct = default)
    {
        using var activity = StorageInstruments.ActivitySource.StartActivity("S3.HeadObject");
        activity?.SetTag("s3.key", objectKey);
        var sw = Stopwatch.StartNew();

        var client = clientPool.GetClient();
        var response = await client.HeadObjectAsync(_opts.BucketName, objectKey, null, ct);

        sw.Stop();
        StorageInstruments.S3Operations.Add(1,
            new KeyValuePair<string, object?>("operation", "head"),
            new KeyValuePair<string, object?>("status", response.IsSuccess ? "success" : "failed"));
        StorageInstruments.S3OperationDuration.Record(sw.Elapsed.TotalMilliseconds, new KeyValuePair<string, object?>("operation", "head"));

        return response.IsSuccess;
    }

    public async Task<S3FileMetadata?> HeadFileAsync(string objectKey, CancellationToken ct = default)
    {
        using var activity = StorageInstruments.ActivitySource.StartActivity("S3.HeadFile");
        activity?.SetTag("s3.key", objectKey);
        var sw = Stopwatch.StartNew();

        var client = clientPool.GetClient();
        var response = await client.HeadObjectAsync(_opts.BucketName, objectKey, null, ct);

        sw.Stop();
        StorageInstruments.S3Operations.Add(1,
            new KeyValuePair<string, object?>("operation", "head"),
            new KeyValuePair<string, object?>("status", response.IsSuccess ? "success" : "failed"));
        StorageInstruments.S3OperationDuration.Record(sw.Elapsed.TotalMilliseconds, new KeyValuePair<string, object?>("operation", "head"));

        if (!response.IsSuccess) return null;
        return new S3FileMetadata
        {
            ContentLength = response.ContentLength,
            ContentType   = response.ContentType,
            ETag          = response.ETag,
            CacheControl  = response.CacheControl
        };
    }

    public async Task<bool> DeleteFileAsync(string objectKey, CancellationToken ct = default)
    {
        using var activity = StorageInstruments.ActivitySource.StartActivity("S3.DeleteObject");
        activity?.SetTag("s3.key", objectKey);
        var sw = Stopwatch.StartNew();

        var client = clientPool.GetClient();
        var response = await client.DeleteObjectAsync(_opts.BucketName, objectKey, null, ct);

        sw.Stop();
        StorageInstruments.S3Operations.Add(1,
            new KeyValuePair<string, object?>("operation", "delete"),
            new KeyValuePair<string, object?>("status", response.IsSuccess ? "success" : "failed"));
        StorageInstruments.S3OperationDuration.Record(sw.Elapsed.TotalMilliseconds, new KeyValuePair<string, object?>("operation", "delete"));

        return response.IsSuccess;
    }

    public string GetFileDownloadUrl(Guid fileId)
        => _opts.Cdn.BuildFileUrl(fileId);

    public string GetDownloadUrl(string objectKey)
        => _opts.Cdn.BuildKeyUrl(objectKey);

    public async Task<Stream?> GetObjectStreamAsync(string objectKey, CancellationToken ct = default)
    {
        using var activity = StorageInstruments.ActivitySource.StartActivity("S3.GetObject");
        activity?.SetTag("s3.key", objectKey);
        var sw = Stopwatch.StartNew();

        var client = clientPool.GetClient();
        var response = await client.GetObjectAsync(_opts.BucketName, objectKey, null, ct);

        sw.Stop();
        StorageInstruments.S3Operations.Add(1,
            new KeyValuePair<string, object?>("operation", "get"),
            new KeyValuePair<string, object?>("status", response.IsSuccess ? "success" : "failed"));
        StorageInstruments.S3OperationDuration.Record(sw.Elapsed.TotalMilliseconds, new KeyValuePair<string, object?>("operation", "get"));

        if (!response.IsSuccess) return null;

        var ms = new MemoryStream(response.ContentLength > 0 ? (int)response.ContentLength : 4096);
        await response.Content.CopyToAsync(ms, ct);
        ms.Position = 0;
        return ms;
    }

    public async Task<Stream?> OpenReadAsync(string objectKey, CancellationToken ct = default)
    {
        using var activity = StorageInstruments.ActivitySource.StartActivity("S3.GetObject");
        activity?.SetTag("s3.key", objectKey);
        var sw = Stopwatch.StartNew();

        var client = clientPool.GetClient();
        var response = await client.GetObjectAsync(_opts.BucketName, objectKey, null, ct);

        sw.Stop();
        StorageInstruments.S3Operations.Add(1,
            new KeyValuePair<string, object?>("operation", "get"),
            new KeyValuePair<string, object?>("status", response.IsSuccess ? "success" : "failed"));
        StorageInstruments.S3OperationDuration.Record(sw.Elapsed.TotalMilliseconds, new KeyValuePair<string, object?>("operation", "get"));

        return response.IsSuccess ? response.Content : null;
    }

    public async Task<Stream?> OpenReadRangeAsync(string objectKey, long from, long toInclusive, CancellationToken ct = default)
    {
        using var activity = StorageInstruments.ActivitySource.StartActivity("S3.GetObjectRange");
        activity?.SetTag("s3.key", objectKey);
        var sw = Stopwatch.StartNew();

        var client = clientPool.GetClient();
        var response = await client.GetObjectAsync(_opts.BucketName, objectKey, r => r.Range.Add(from, toInclusive), ct);

        Record("get_range", response.IsSuccess, sw);

        return response.IsSuccess ? response.Content : null;
    }

    public async Task<string?> CreateMultipartUploadAsync(string objectKey, string contentType, string? cacheControl, CancellationToken ct = default)
    {
        using var activity = StorageInstruments.ActivitySource.StartActivity("S3.CreateMultipartUpload");
        activity?.SetTag("s3.key", objectKey);
        var sw = Stopwatch.StartNew();

        var response = await clientPool.GetMultipartClient().CreateMultipartUploadAsync(_opts.BucketName, objectKey, r =>
        {
            r.ContentType.Set(contentType);
            if (cacheControl is not null)
                r.SetHeader("Cache-Control", cacheControl);
        }, ct);

        Record("create_multipart", response.IsSuccess, sw);

        return response.IsSuccess ? response.UploadId : null;
    }

    public async Task<bool> CompleteMultipartUploadAsync(string objectKey, string uploadId, IReadOnlyList<(int PartNumber, string ETag)> parts,
        CancellationToken ct = default)
    {
        using var activity = StorageInstruments.ActivitySource.StartActivity("S3.CompleteMultipartUpload");
        activity?.SetTag("s3.key", objectKey);
        var sw = Stopwatch.StartNew();

        var infos = new List<S3PartInfo>(parts.Count);
        foreach (var (number, etag) in parts)
            infos.Add(new S3PartInfo(etag, number));

        var response = await clientPool.GetMultipartClient().CompleteMultipartUploadAsync(_opts.BucketName, objectKey, uploadId, infos, null, ct);

        Record("complete_multipart", response.IsSuccess, sw);

        return response.IsSuccess;
    }

    public async Task<bool> AbortMultipartUploadAsync(string objectKey, string uploadId, CancellationToken ct = default)
    {
        using var activity = StorageInstruments.ActivitySource.StartActivity("S3.AbortMultipartUpload");
        activity?.SetTag("s3.key", objectKey);
        var sw = Stopwatch.StartNew();

        var response = await clientPool.GetMultipartClient().AbortMultipartUploadAsync(_opts.BucketName, objectKey, uploadId, null, ct);

        Record("abort_multipart", response.IsSuccess, sw);

        return response.IsSuccess;
    }

    private static void Record(string operation, bool success, Stopwatch sw)
    {
        sw.Stop();
        StorageInstruments.S3Operations.Add(1,
            new KeyValuePair<string, object?>("operation", operation),
            new KeyValuePair<string, object?>("status", success ? "success" : "failed"));
        StorageInstruments.S3OperationDuration.Record(sw.Elapsed.TotalMilliseconds, new KeyValuePair<string, object?>("operation", operation));
    }

    public async Task<bool> PutObjectAsync(string objectKey, Stream content, string? contentType = null, string? cacheControl = null,
        CancellationToken ct = default)
    {
        using var activity = StorageInstruments.ActivitySource.StartActivity("S3.PutObject");
        activity?.SetTag("s3.key", objectKey);
        var sw = Stopwatch.StartNew();

        var client = clientPool.GetClient();
        Action<Genbox.SimpleS3.Core.Network.Requests.Objects.PutObjectRequest>? config = null;
        if (contentType is not null || cacheControl is not null)
            config = r =>
            {
                if (contentType is not null)
                    r.ContentType.Set(contentType);
                // Verbatim: the library's builder writes its own spelling of the directives.
                if (cacheControl is not null)
                    r.SetHeader("Cache-Control", cacheControl);
            };

        var response = await client.PutObjectAsync(_opts.BucketName, objectKey, content, config, ct);

        sw.Stop();
        StorageInstruments.S3Operations.Add(1,
            new KeyValuePair<string, object?>("operation", "put"),
            new KeyValuePair<string, object?>("status", response.IsSuccess ? "success" : "failed"));
        StorageInstruments.S3OperationDuration.Record(sw.Elapsed.TotalMilliseconds, new KeyValuePair<string, object?>("operation", "put"));

        return response.IsSuccess;
    }
}
