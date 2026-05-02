namespace Argon.Features.Storage;

using System.Diagnostics;
using System.Globalization;
using Genbox.SimpleS3.Core.Abstracts.Clients;

public interface IS3StorageService
{
    Task<bool> FileExistsAsync(string objectKey, CancellationToken ct = default);
    Task<S3FileMetadata?> HeadFileAsync(string objectKey, CancellationToken ct = default);
    Task<bool> DeleteFileAsync(string objectKey, CancellationToken ct = default);
    string GetPublicUrl(string objectKey);
    string GeneratePresignedGetUrl(string objectKey, int expirationSeconds = 3600);
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

    public string GetPublicUrl(string objectKey)
        => $"{_opts.PublicBaseUrl.TrimEnd('/')}/{objectKey}";

    public string GeneratePresignedGetUrl(string objectKey, int expirationSeconds = 3600)
    {
        // Generate presigned GET URL using AWS Signature V4 query string auth
        var now       = DateTime.UtcNow;
        var dateStamp = now.ToString("yyyyMMdd", CultureInfo.InvariantCulture);
        var amzDate   = now.ToString("yyyyMMdd'T'HHmmss'Z'", CultureInfo.InvariantCulture);
        var credential = $"{_opts.AccessKey}/{dateStamp}/{_opts.Region}/s3/aws4_request";

        var host = _opts.Endpoint;
        var canonicalUri = $"/{_opts.BucketName}/{objectKey}";

        var queryParams = new SortedDictionary<string, string>
        {
            ["X-Amz-Algorithm"]     = "AWS4-HMAC-SHA256",
            ["X-Amz-Credential"]    = credential,
            ["X-Amz-Date"]          = amzDate,
            ["X-Amz-Expires"]       = expirationSeconds.ToString(CultureInfo.InvariantCulture),
            ["X-Amz-SignedHeaders"]  = "host"
        };

        var canonicalQueryString = string.Join("&",
            queryParams.Select(kvp => $"{Uri.EscapeDataString(kvp.Key)}={Uri.EscapeDataString(kvp.Value)}"));

        var canonicalHeaders = $"host:{host}\n";
        var signedHeaders    = "host";

        var canonicalRequest = $"GET\n{canonicalUri}\n{canonicalQueryString}\n{canonicalHeaders}\n{signedHeaders}\nUNSIGNED-PAYLOAD";

        var canonicalRequestHash = HashSha256(canonicalRequest);
        var stringToSign = $"AWS4-HMAC-SHA256\n{amzDate}\n{dateStamp}/{_opts.Region}/s3/aws4_request\n{canonicalRequestHash}";

        var kDate    = HmacSha256(Encoding.UTF8.GetBytes($"AWS4{_opts.SecretKey}"), dateStamp);
        var kRegion  = HmacSha256(kDate, _opts.Region);
        var kService = HmacSha256(kRegion, "s3");
        var kSigning = HmacSha256(kService, "aws4_request");
        var signature = Convert.ToHexString(HmacSha256(kSigning, stringToSign)).ToLowerInvariant();

        var scheme = _opts.UseSsl ? "https" : "http";
        return $"{scheme}://{host}{canonicalUri}?{canonicalQueryString}&X-Amz-Signature={signature}";
    }

    private static string HashSha256(string data)
    {
        var bytes = SHA256.HashData(Encoding.UTF8.GetBytes(data));
        return Convert.ToHexString(bytes).ToLowerInvariant();
    }

    private static byte[] HmacSha256(byte[] key, string data)
    {
        using var hmac = new HMACSHA256(key);
        return hmac.ComputeHash(Encoding.UTF8.GetBytes(data));
    }
}
