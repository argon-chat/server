namespace Argon.Api.Features.MediaStorage;

using Contracts;

public interface IContentDeliveryNetwork
{
    IContentStorage Storage { get; }

    ValueTask<Maybe<UploadError>> CreateAssetAsync(StorageNameSpace ns, AssetId asset, IFormFile file)
    {
        var memory = file.OpenReadStream();
        return CreateAssetAsync(ns, asset, memory);
    }

    ValueTask<Maybe<UploadError>> ReplaceAssetAsync(StorageNameSpace ns, AssetId asset, IFormFile file)
    {
        using var memory = file.OpenReadStream();
        return ReplaceAssetAsync(ns, asset, memory);
    }

    ValueTask<Maybe<UploadError>> CreateAssetAsync(StorageNameSpace ns, AssetId asset, Stream file);
    ValueTask<Maybe<UploadError>> ReplaceAssetAsync(StorageNameSpace ns, AssetId asset, Stream file);
    ValueTask<string>             GenerateAssetUrl(StorageNameSpace ns, AssetId asset);
}