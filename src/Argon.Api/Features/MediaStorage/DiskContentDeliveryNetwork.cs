namespace Argon.Api.Features.MediaStorage;

using Contracts;

public class DiskContentDeliveryNetwork([FromKeyedServices(IContentStorage.DiskContentStorageKey)] IContentStorage storage,
    ILogger<YandexContentDeliveryNetwork> logger) : IContentDeliveryNetwork
{
    public IContentStorage               Storage { get; } = storage;
    public async ValueTask<Maybe<UploadError>> CreateAssetAsync(StorageNameSpace ns, AssetId asset, Stream file)
    {
        try
        {
            await Storage.UploadFile(ns, asset, file);
            return Maybe<UploadError>.None();
        }
        catch (Exception e)
        {
            logger.LogCritical(e, $"Failed upload file '{asset.GetFilePath()}'");
            return UploadError.INTERNAL_ERROR;
        }
    }

    public ValueTask<Maybe<UploadError>> ReplaceAssetAsync(StorageNameSpace ns, AssetId asset, Stream file)
        => throw new NotImplementedException();

    public ValueTask<string> GenerateAssetUrl(StorageNameSpace ns, AssetId asset)
        => new($"/files/{ns.ToPath()}/{asset.GetFilePath()}?nocache=1");
}