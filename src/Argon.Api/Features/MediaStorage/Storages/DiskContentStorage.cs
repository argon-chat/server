namespace Argon.Features.MediaStorage.Storages;

public class DiskContentStorage : IContentStorage
{
    public ValueTask<StorageSpace> GetStorageStats()
        => new(new StorageSpace(0, 0, 0));

    public async ValueTask UploadFile(StorageNameSpace block, AssetId assetId, Stream data)
    {
        var fullPath  = $"./storage/{block.ToPath()}/{assetId.GetFilePath()}";
        var directory = new FileInfo(fullPath).Directory!;

        if (!directory.Exists)
            directory.Create();


        await using var stream = File.OpenWrite(fullPath);
        await data.CopyToAsync(stream);
    }

    public async ValueTask DeleteFile(StorageNameSpace block, AssetId assetId)
    {
        if (File.Exists($"./storage/{block.ToPath()}/{assetId.GetFilePath()}"))
            File.Delete($"./storage/{block.ToPath()}/{assetId.GetFilePath()}");
    }

    public static Stream OpenFileRead(StorageNameSpace block, AssetId assetId)
        => File.OpenRead($"./storage/{block.ToPath()}/{assetId.GetFilePath()}");
}