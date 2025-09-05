namespace Argon.Features.MediaStorage;


public abstract class AssetId(Guid assetId, string extensions)
{
    public string ToFileId()
        => $"{assetId:N}.{extensions}";

    public abstract string GetFilePath();


    public static AssetId Avatar(Guid userId)
        => new UserAssetId(userId, Guid.NewGuid(), "png");

    public static AssetId Avatar(Guid userId, string extensions)
        => new UserAssetId(userId, Guid.NewGuid(), extensions);

    public static AssetId ServerFile(Guid serverId, string extension)
        => new UserAssetId(serverId, Guid.NewGuid(), extension);
}

public sealed class UserAssetId(Guid userId, Guid assetId, string extensions) : AssetId(assetId, extensions)
{
    public override string GetFilePath()
        => $"user/{userId:N}/{ToFileId()}";
}
public sealed class ServerAssetId(Guid serverId, Guid assetId, string extensions) : AssetId(assetId, extensions)
{
    public override string GetFilePath()
        => $"server/{serverId:N}/{ToFileId()}";
}
public sealed class ChannelAssetId(Guid serverId, Guid channelId, Guid assetId, string extensions) : AssetId(assetId, extensions)
{
    public override string GetFilePath()
        => $"server/{serverId:N}/channel/{channelId:N}/{ToFileId()}";
}