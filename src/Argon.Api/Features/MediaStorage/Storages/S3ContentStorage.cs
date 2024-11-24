namespace Argon.Api.Features.MediaStorage.Storages;

using Genbox.SimpleS3.Core.Abstracts.Clients;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;

public class S3ContentStorage([FromKeyedServices("GenericS3:client")] IObjectClient s3Client, IOptions<StorageOptions> options, 
    ILogger<IContentStorage> logger) : IContentStorage
{
    public ValueTask<StorageSpace> GetStorageStats()
        => new(new StorageSpace(0, 0, 0));

    public async ValueTask UploadFile(StorageNameSpace block, AssetId assetId, Stream data)
    {
        var config = options.Value;
        logger.LogInformation("Begin upload file to s3 storage, '{bucketName}' to '{path}'", 
            config.BucketName, $"{block.ToPath()}/{assetId.GetFilePath()}");
        var result = await s3Client.PutObjectAsync(config.BucketName, $"{block.ToPath()}/{assetId.GetFilePath()}", data, request => {
            foreach (var (key, value) in assetId.GetTags(block))
                request.Tags.Add(key, value);
        });

        if (!result.IsSuccess)
        {
            logger.LogCritical("Failed upload file to s3 storage, '{bucketName}' to '{path}', errorCode: {errorCode}, errorMessage: {errorMessage}",
                config.BucketName, $"{block.ToPath()}/{assetId.GetFilePath()}", result.Error?.Code, result.Error?.Message);
            throw new InvalidOperationException();
        }
    }

    public async ValueTask DeleteFile(StorageNameSpace block, AssetId assetId)
    {
        var config = options.Value;
        var result = await s3Client.DeleteObjectAsync(config.BucketName, $"/{block.ToPath()}/{assetId.GetFilePath()}");

        if (!result.IsSuccess)
            throw new InvalidOperationException();
    }
}