namespace Argon.Api.Features.MediaStorage.Storages;

using Genbox.SimpleS3.Core.Abstracts.Clients;
using Microsoft.Extensions.Options;

public class S3ContentStorage([FromKeyedServices("GenericS3:container")] IObjectClient s3Client, IOptions<StorageOptions> options) : IContentStorage
{
    public ValueTask<StorageSpace> GetStorageStats()
        => new(new StorageSpace(0, 0, 0));

    public async ValueTask UploadFile(StorageNameSpace block, AssetId assetId, Stream data)
    {
        var config = options.Value;
        var result = await s3Client.PutObjectAsync(config.BucketName, $"/{block.ToPath()}/{assetId.GetFilePath()}", data, request => {
            foreach (var (key, value) in assetId.GetTags(block))
                request.Tags.Add(key, value);
        });

        if (!result.IsSuccess)
            throw new InvalidOperationException();
    }

    public async ValueTask DeleteFile(StorageNameSpace block, AssetId assetId)
    {
        var config = options.Value;
        var result = await s3Client.DeleteObjectAsync(config.BucketName, $"/{block.ToPath()}/{assetId.GetFilePath()}");

        if (!result.IsSuccess)
            throw new InvalidOperationException();
    }
}