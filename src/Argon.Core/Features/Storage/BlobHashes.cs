namespace Argon.Features.Storage;

using System.Buffers;

public static class BlobHashes
{
    /// <summary>The MD5 an ETag carries for a single-part PUT; null for a multipart ETag or anything else.</summary>
    public static byte[]? ParseEtagMd5(string? etag)
    {
        if (string.IsNullOrEmpty(etag))
            return null;

        var span = etag.AsSpan().Trim().Trim('"');
        if (span.Length != 32)
            return null;

        Span<byte> md5 = stackalloc byte[16];
        return Convert.FromHexString(span, md5, out _, out var written) == OperationStatus.Done && written == 16
            ? md5.ToArray()
            : null;
    }

    /// <summary>The media type as an identity: lower case, parameters dropped, never empty.</summary>
    public static string NormalizeContentType(string? contentType)
    {
        if (string.IsNullOrWhiteSpace(contentType))
            return "application/octet-stream";

        var span = contentType.AsSpan();
        var semicolon = span.IndexOf(';');
        if (semicolon >= 0)
            span = span[..semicolon];

        return span.Trim().ToString().ToLowerInvariant();
    }
}
