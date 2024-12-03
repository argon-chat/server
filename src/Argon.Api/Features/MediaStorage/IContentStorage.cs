namespace Argon.Features.MediaStorage;

public interface IContentStorage
{
    ValueTask<StorageSpace> GetStorageStats();

    ValueTask UploadFile(StorageNameSpace block, AssetId assetId, Stream data);

    ValueTask DeleteFile(StorageNameSpace block, AssetId assetId);


    public const string GenericS3StorageKey   = "cdn:bucket:s3";
    public const string InMemoryStorageKey    = "cdn:bucket:inmemory";
    public const string DiskContentStorageKey = "cdn:bucket:disk";
}


public record struct StorageNameSpace(string path, Guid id)
{
    public string ToPath() => $"{path}/{id:N}";

    public static StorageNameSpace ForServer(Guid serverId) => new("servers", serverId);
    public static StorageNameSpace ForUser(Guid userId) => new("users", userId);
}


public enum StorageKind
{
    InMemory,
    Disk,
    GenericS3
}

public class StorageOptions
{
    public StorageKind Kind       { get; set; }
    public string      BaseUrl    { get; set; }
    public string      Login      { get; set; }
    public string      Region     { get; set; }
    public string      Password   { get; set; }
    public string      BucketName { get; set; }
}
