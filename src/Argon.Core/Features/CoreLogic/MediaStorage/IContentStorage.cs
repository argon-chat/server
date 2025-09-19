namespace Argon.Features.MediaStorage;

public interface IContentStorage
{
    ValueTask<StorageSpace> GetStorageStats();

    ValueTask UploadFile(AssetId assetId, Stream data);

    ValueTask DeleteFile(AssetId assetId);


    public const string GenericS3StorageKey   = "cdn:bucket:s3";
    public const string InMemoryStorageKey    = "cdn:bucket:inmemory";
    public const string DiskContentStorageKey = "cdn:bucket:disk";
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
    public bool        EnableTags { get; set; }
}
