namespace Argon.Features.MediaStorage;

using System.Web;

public class YandexContentDeliveryNetwork([FromKeyedServices(IContentStorage.GenericS3StorageKey)] IContentStorage storage,
    ILogger<YandexContentDeliveryNetwork> logger, IOptions<CdnOptions> options)
    : IContentDeliveryNetwork
{
    public IContentStorage Storage { get; } = storage;
    public CdnOptions      Config  => options.Value;

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
        => GenerateSignedLink(Config.BaseUrl, $"{asset.GetFilePath()}", Config.SignSecret, (int)Config.EntryExpire.TotalSeconds);

    private static string GenerateSignedLink(
        string hostname,
        string path,
        string secret,
        int expiryInSeconds)
    {
        var expires   = DateTimeOffset.UtcNow.ToUnixTimeSeconds() + expiryInSeconds;
        var tokenData = $"{expires}{path} {secret}";

        using var md5       = MD5.Create();
        var       hashBytes = md5.ComputeHash(Encoding.UTF8.GetBytes(tokenData));
        var token = Convert.ToBase64String(hashBytes)
           .Replace("\n", "")
           .Replace("+", "-")
           .Replace("/", "_")
           .Replace("=", "");

        var query = HttpUtility.ParseQueryString(string.Empty);
        query["md5"]     = token;
        query["expires"] = expires.ToString();

        return $"{hostname}{path}?{query}";
    }
}