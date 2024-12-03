namespace Argon.Features.MediaStorage;

public readonly struct AssetId(Guid assetId, AssetScope scope, AssetKind kind, string extensions)
{
    public string ToFileId()
        => $"{assetId:D}-{((byte)scope):X2}-{((byte)kind):X2}-00.{extensions}"; // last two zero reserved

    public string GetFilePath()
    {
        if (scope == AssetScope.ProfileAsset)
            return $"profile/{assetId.ToString().Substring(0, 8)}/{ToFileId()}";
        if (scope == AssetScope.ChatAsset)
            return $"chat/{assetId.ToString().Substring(0, 8)}/{ToFileId()}";
        if (scope == AssetScope.ServiceAsset)
            return $"service/{assetId.ToString().Substring(0, 8)}/{ToFileId()}";
        return $"temp/{ToFileId()}";
    }

    public Dictionary<string, string> GetTags(StorageNameSpace @namespace)
    {
        var tags = new Dictionary<string, string>
        {
            {
                nameof(AssetScope), $"{scope}"
            },
            {
                nameof(AssetKind), $"{kind}"
            },
            {
                $"Id", $"{assetId}"
            },
            {
                $"Namespace", $"{@namespace.path}:{@namespace.id}"
            }
        };
        return tags;
    }

    public static AssetId FromFileId(string fileId)
    {
        if (fileId.Length < 46)
            throw new InvalidOperationException("Bad file id");
        var span  = fileId.AsSpan();
        var guid  = Guid.Parse(span.Slice(0, 36));
        var scope = byte.Parse(span.Slice(37, 2));
        var kind  = byte.Parse(span.Slice(40, 2));
        var ext   = fileId.Split('.').Last();
        return new AssetId(guid, (AssetScope)scope, (AssetKind)kind, ext);
    }

    public static AssetId Avatar()      => new(Guid.NewGuid(), AssetScope.ProfileAsset, AssetKind.Image, "png");
    public static AssetId VideoAvatar() => new(Guid.NewGuid(), AssetScope.ProfileAsset, AssetKind.VideoNoSound, "mp4");
}

public enum AssetScope : byte
{
    ProfileAsset,
    ChatAsset,
    ServiceAsset
}

public enum AssetKind : byte
{
    Image,
    Video,        // only png
    VideoNoSound, // gif
    File,
    ServerContent,
    ServiceContent,
    Sound
}