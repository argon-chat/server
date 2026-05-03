namespace Argon.Features.Storage;

using System.Diagnostics;
using Genbox.SimpleS3.Core.Abstracts.Clients;

public interface IS3StorageService
{
    Task<bool> FileExistsAsync(string objectKey, CancellationToken ct = default);
    Task<S3FileMetadata?> HeadFileAsync(string objectKey, CancellationToken ct = default);
    Task<bool> DeleteFileAsync(string objectKey, CancellationToken ct = default);
    string GetDownloadUrl(string objectKey, string? countryCode = null);
}

public class S3FileMetadata
{
    public long   ContentLength { get; init; }
    public string? ContentType  { get; init; }
    public string? ETag         { get; init; }
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
            ETag          = response.ETag
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

    public string GetDownloadUrl(string objectKey, string? countryCode = null)
        => _opts.Cdn.GetDownloadUrl(objectKey, countryCode);
}
