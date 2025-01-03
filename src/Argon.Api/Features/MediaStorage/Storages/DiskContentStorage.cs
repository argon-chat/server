namespace Argon.Features.MediaStorage.Storages;

public class DiskContentStorage : IContentStorage
{
    public ValueTask<StorageSpace> GetStorageStats()
        => new(new StorageSpace(0, 0, 0));

    public async ValueTask UploadFile(AssetId assetId, Stream data)
    {
        var fullPath  = $"./storage/{assetId.GetFilePath()}";
        var directory = new FileInfo(fullPath).Directory!;

        if (!directory.Exists)
            directory.Create();


        await using var stream = File.OpenWrite(fullPath);
        await data.CopyToAsync(stream);
    }

    public async ValueTask DeleteFile(AssetId assetId)
    {
        if (File.Exists($"./storage/{assetId.GetFilePath()}"))
            File.Delete($"./storage/{assetId.GetFilePath()}");
    }

    public static Stream OpenFileRead(AssetId assetId)
        => File.OpenRead($"./storage/{assetId.GetFilePath()}");
}