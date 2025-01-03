namespace Argon.Features.MediaStorage;

public class DiskContentDeliveryNetwork(
    [FromKeyedServices(IContentStorage.DiskContentStorageKey)] IContentStorage storage,
    ILogger<YandexContentDeliveryNetwork> logger) : IContentDeliveryNetwork
{
    public IContentStorage Storage { get; } = storage;

    public async ValueTask<Maybe<UploadError>> CreateAssetAsync(AssetId asset, Stream file)
    {
        try
        {
            await Storage.UploadFile(asset, file);
            return Maybe<UploadError>.None();
        }
        catch (Exception e)
        {
            logger.LogCritical(e, $"Failed upload file '{asset.GetFilePath()}'");
            return UploadError.INTERNAL_ERROR;
        }
    }

    public ValueTask<Maybe<UploadError>> ReplaceAssetAsync(AssetId asset, Stream file)
        => throw new NotImplementedException();

    public string GenerateAssetUrl(AssetId asset)
        => new($"/files/{asset.GetFilePath()}?nocache=1");
}