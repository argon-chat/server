namespace Argon.Api.Features.MediaStorage;

public class CdnOptions
{
    public string         BaseUrl     { get; set; }
    public TimeSpan       EntryExpire { get; set; }
    public bool           SignUrl     { get; set; }
    public string         SignSecret  { get; set; }
    public StorageOptions Storage     { get; set; }
}

public readonly record struct StorageSpace(ulong total, ulong current, uint free);

public enum UploadError
{
    NONE,
    INTERNAL_ERROR
}