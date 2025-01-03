namespace Argon.Features.MediaStorage;

public interface IContentDeliveryNetwork
{
    IContentStorage Storage { get; }

    ValueTask<Maybe<UploadError>> CreateAssetAsync(AssetId asset, IFormFile file)
    {
        var memory = file.OpenReadStream();
        return CreateAssetAsync(asset, memory);
    }

    ValueTask<Maybe<UploadError>> ReplaceAssetAsync(AssetId asset, IFormFile file)
    {
        using var memory = file.OpenReadStream();
        return ReplaceAssetAsync(asset, memory);
    }

    ValueTask<Maybe<UploadError>> CreateAssetAsync(AssetId asset, Stream file);
    ValueTask<Maybe<UploadError>> ReplaceAssetAsync(AssetId asset, Stream file);
    string                        GenerateAssetUrl(AssetId asset);
}