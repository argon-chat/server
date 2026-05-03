namespace Argon.Features.Storage;

using System.Globalization;
using System.Security.Cryptography;
using System.Text;

/// <summary>
///     Generates S3-compatible presigned PUT URLs using AWS Signature V4 (query string auth).
///     Compatible with Backblaze B2 S3 API.
/// </summary>
public class S3PresignedUrlGenerator(IOptions<StorageOptions> options)
{
    private readonly StorageOptions _opts = options.Value;

    public PresignedUploadData GeneratePresignedPut(
        string  objectKey,
        string? contentType      = null,
        int     expirationSeconds = 600)
    {
        var now       = DateTime.UtcNow;
        var dateStamp = now.ToString("yyyyMMdd", CultureInfo.InvariantCulture);
        var amzDate   = now.ToString("yyyyMMdd'T'HHmmss'Z'", CultureInfo.InvariantCulture);
        var credential = $"{_opts.AccessKey}/{dateStamp}/{_opts.Region}/s3/aws4_request";

        var scheme   = _opts.UseSsl ? "https" : "http";
        var host     = $"{_opts.BucketName}.{_opts.Endpoint}";
        var path     = $"/{objectKey}";

        // Signed headers
        var signedHeaders = string.IsNullOrEmpty(contentType) ? "host" : "content-type;host";

        // Query parameters (must be sorted)
        var queryParams = new SortedDictionary<string, string>(StringComparer.Ordinal)
        {
            ["X-Amz-Algorithm"]     = "AWS4-HMAC-SHA256",
            ["X-Amz-Credential"]    = credential,
            ["X-Amz-Date"]          = amzDate,
            ["X-Amz-Expires"]       = expirationSeconds.ToString(CultureInfo.InvariantCulture),
            ["X-Amz-SignedHeaders"]  = signedHeaders
        };

        var canonicalQueryString = string.Join("&",
            queryParams.Select(kv => $"{Uri.EscapeDataString(kv.Key)}={Uri.EscapeDataString(kv.Value)}"));

        // Canonical headers
        var canonicalHeaders = string.IsNullOrEmpty(contentType)
            ? $"host:{host}\n"
            : $"content-type:{contentType}\nhost:{host}\n";

        // Canonical request
        var canonicalRequest = string.Join("\n",
            "PUT",
            path,
            canonicalQueryString,
            canonicalHeaders,
            signedHeaders,
            "UNSIGNED-PAYLOAD");

        // String to sign
        var scope        = $"{dateStamp}/{_opts.Region}/s3/aws4_request";
        var stringToSign = string.Join("\n",
            "AWS4-HMAC-SHA256",
            amzDate,
            scope,
            HexHash(canonicalRequest));

        // Signature
        var signature = CalculateSignature(dateStamp, stringToSign);

        var url = $"{scheme}://{host}{path}?{canonicalQueryString}&X-Amz-Signature={signature}";

        // Headers the client must set on the PUT request
        var headers = new Dictionary<string, string>();
        if (!string.IsNullOrEmpty(contentType))
            headers["Content-Type"] = contentType;

        return new PresignedUploadData
        {
            Url     = url,
            Headers = headers
        };
    }

    private string CalculateSignature(string dateStamp, string stringToSign)
    {
        var kDate    = HmacSha256(Encoding.UTF8.GetBytes($"AWS4{_opts.SecretKey}"), dateStamp);
        var kRegion  = HmacSha256(kDate, _opts.Region);
        var kService = HmacSha256(kRegion, "s3");
        var kSigning = HmacSha256(kService, "aws4_request");

        var signatureBytes = HmacSha256(kSigning, stringToSign);
        return Convert.ToHexString(signatureBytes).ToLowerInvariant();
    }

    private static string HexHash(string data)
    {
        var hash = SHA256.HashData(Encoding.UTF8.GetBytes(data));
        return Convert.ToHexString(hash).ToLowerInvariant();
    }

    private static byte[] HmacSha256(byte[] key, string data)
    {
        using var hmac = new HMACSHA256(key);
        return hmac.ComputeHash(Encoding.UTF8.GetBytes(data));
    }
}

public class PresignedUploadData
{
    public required string                     Url     { get; init; }
    public required Dictionary<string, string> Headers { get; init; }
}
