namespace Argon.Features.MediaStorage.Storages;

using Genbox.SimpleS3.Core.Abstracts.Clients;
using Microsoft.Extensions.Logging;

public class S3ContentStorage(IServiceProvider provider, IOptions<StorageOptions> options, 
    ILogger<IContentStorage> logger) : IContentStorage
{
    public ValueTask<StorageSpace> GetStorageStats()
        => new(new StorageSpace(0, 0, 0));

    public async ValueTask UploadFile(AssetId assetId, Stream data)
    {
        await using var scope    = provider.CreateAsyncScope();
        var             s3Client = scope.ServiceProvider.GetRequiredKeyedService<IObjectClient>("GenericS3:client");
        var             config   = options.Value;
        logger.LogInformation("Begin upload file to s3 storage, '{bucketName}' to '{path}'", 
            config.BucketName, $"{assetId.GetFilePath()}");
        var result = await s3Client.PutObjectAsync(config.BucketName, $"{assetId.GetFilePath()}", data);

        if (!result.IsSuccess)
        {
            logger.LogCritical("Failed upload file to s3 storage, '{bucketName}' to '{path}', errorCode: {errorCode}, errorMessage: {errorMessage}",
                config.BucketName, $"{assetId.GetFilePath()}", result.Error?.Code, result.Error?.Message);
            throw new InvalidOperationException();
        }
    }

    public async ValueTask DeleteFile(AssetId assetId)
    {
        await using var scope    = provider.CreateAsyncScope();
        var             s3Client = scope.ServiceProvider.GetRequiredKeyedService<IObjectClient>("GenericS3:client");
        var             config   = options.Value;
        var             result   = await s3Client.DeleteObjectAsync(config.BucketName, $"{assetId.GetFilePath()}");

        if (!result.IsSuccess)
            throw new InvalidOperationException();
    }
}