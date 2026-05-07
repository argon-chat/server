namespace Argon.Features.Storage;

using Genbox.SimpleS3.Core.Abstracts.Clients;
using Genbox.SimpleS3.Core.Network.Requests.Objects;
using Genbox.SimpleS3.Core.Network.Responses.Objects;

public interface IExportS3Service
{
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

    public async Task<List<string>> ListObjectsAsync(string prefix, CancellationToken ct = default)
    {
        var client = clientPool.GetClient();
        var keys = new List<string>();

        var response = await client.ListObjectsAsync(BucketName, req => req.Prefix = prefix, ct);

        if (response.IsSuccess && response.Objects != null)
        {
            foreach (var obj in response.Objects)
                keys.Add(obj.ObjectKey);
        }

        return keys;
    }

    public async Task DeletePrefixAsync(string prefix, CancellationToken ct = default)
    {
        var keys = await ListObjectsAsync(prefix, ct);
        var client = clientPool.GetClient();

        foreach (var key in keys)
            await client.DeleteObjectAsync(BucketName, key, null, ct);
    }

    public string GeneratePresignedGetUrl(string objectKey, int expirationSeconds = 172800)
        => presignedUrlGenerator.GeneratePresignedGet(BucketName, objectKey, expirationSeconds);
}
