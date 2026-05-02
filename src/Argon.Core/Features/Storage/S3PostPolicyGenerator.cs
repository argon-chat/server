namespace Argon.Features.Storage;

using System.Globalization;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;

/// <summary>
///     Generates S3-compatible presigned POST policies with content-length-range enforcement.
///     Implements AWS Signature V4 for POST (browser-based uploads).
/// </summary>
public class S3PostPolicyGenerator(IOptions<StorageOptions> options)
{
    private readonly StorageOptions _opts = options.Value;

    public PresignedPostData GeneratePresignedPost(
        string objectKey,
        long   maxSizeBytes,
        bool   isPublic,
        string? contentTypePrefix = null,
        int    expirationSeconds  = 600)
    {
        var bucketOpts = _opts.GetBucketOptions(isPublic);
        var now        = DateTime.UtcNow;
        var expiration = now.AddSeconds(expirationSeconds);
        var dateStamp  = now.ToString("yyyyMMdd", CultureInfo.InvariantCulture);
        var amzDate    = now.ToString("yyyyMMdd'T'HHmmss'Z'", CultureInfo.InvariantCulture);
        var credential = $"{bucketOpts.AccessKey}/{dateStamp}/{bucketOpts.Region}/s3/aws4_request";

        var conditions = new List<object>
        {
            new Dictionary<string, string> { ["bucket"] = bucketOpts.BucketName },
            new[] { "eq", "$key", objectKey },
            new[] { "content-length-range", "1", maxSizeBytes.ToString(CultureInfo.InvariantCulture) },
            new Dictionary<string, string> { ["x-amz-credential"] = credential },
            new Dictionary<string, string> { ["x-amz-algorithm"] = "AWS4-HMAC-SHA256" },
            new Dictionary<string, string> { ["x-amz-date"] = amzDate }
        };

        if (!string.IsNullOrEmpty(contentTypePrefix))
            conditions.Add(new[] { "starts-with", "$Content-Type", contentTypePrefix });

        var policy = new
        {
            expiration = expiration.ToString("yyyy-MM-dd'T'HH:mm:ss.fff'Z'", CultureInfo.InvariantCulture),
            conditions
        };

        var policyJson   = JsonSerializer.Serialize(policy);
        var policyBase64 = Convert.ToBase64String(Encoding.UTF8.GetBytes(policyJson));
        var signature    = CalculateSignature(bucketOpts, dateStamp, policyBase64);

        var endpoint = bucketOpts.UseSsl ? $"https://{bucketOpts.Endpoint}" : $"http://{bucketOpts.Endpoint}";
        var url      = $"{endpoint}/{bucketOpts.BucketName}";

        var fields = new Dictionary<string, string>
        {
            ["key"]              = objectKey,
            ["policy"]           = policyBase64,
            ["x-amz-algorithm"]  = "AWS4-HMAC-SHA256",
            ["x-amz-credential"] = credential,
            ["x-amz-date"]       = amzDate,
            ["x-amz-signature"]  = signature
        };

        if (!string.IsNullOrEmpty(contentTypePrefix))
            fields["Content-Type"] = contentTypePrefix;

        return new PresignedPostData
        {
            Url    = url,
            Fields = fields
        };
    }

    private static string CalculateSignature(S3BucketOptions bucketOpts, string dateStamp, string policyBase64)
    {
        var kDate    = HmacSha256(Encoding.UTF8.GetBytes($"AWS4{bucketOpts.SecretKey}"), dateStamp);
        var kRegion  = HmacSha256(kDate, bucketOpts.Region);
        var kService = HmacSha256(kRegion, "s3");
        var kSigning = HmacSha256(kService, "aws4_request");

        var signatureBytes = HmacSha256(kSigning, policyBase64);
        return Convert.ToHexString(signatureBytes).ToLowerInvariant();
    }

    private static byte[] HmacSha256(byte[] key, string data)
    {
        using var hmac = new HMACSHA256(key);
        return hmac.ComputeHash(Encoding.UTF8.GetBytes(data));
    }
}

public class PresignedPostData
{
    public required string                     Url    { get; init; }
    public required Dictionary<string, string> Fields { get; init; }
}
