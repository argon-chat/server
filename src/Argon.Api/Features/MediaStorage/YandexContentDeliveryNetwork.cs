namespace Argon.Api.Features.MediaStorage;

using System.Web;
using Contracts;
using Microsoft.Extensions.Options;

public class YandexContentDeliveryNetwork([FromKeyedServices(IContentStorage.GenericS3StorageKey)] IContentStorage storage,
    ILogger<YandexContentDeliveryNetwork> logger, IOptions<CdnOptions> options)
    : IContentDeliveryNetwork
{
    public IContentStorage Storage { get; } = storage;
    public CdnOptions      Config  => options.Value;

    public async ValueTask<Maybe<UploadError>> CreateAssetAsync(StorageNameSpace ns, AssetId asset, Stream file)
    {
        try
        {
            await Storage.UploadFile(ns, asset, file);
            return Maybe<UploadError>.None();
        }
        catch (Exception e)
        {
            logger.LogCritical(e, $"Failed upload file '{asset.GetFilePath()}'");
            return UploadError.INTERNAL_ERROR;
        }
    }

    public ValueTask<Maybe<UploadError>> ReplaceAssetAsync(StorageNameSpace ns, AssetId asset, Stream file)
        => throw new NotImplementedException();

    public ValueTask<string> GenerateAssetUrl(StorageNameSpace ns, AssetId asset)
        => new(GenerateSignedLink(Config.BaseUrl,$"/{ns.ToPath()}/{asset.GetFilePath()}", Config.SignSecret, (int)Config.EntryExpire.TotalSeconds));

    private static string GenerateSignedLink(
        string hostname,
        string path,
        string secret,
        int expiryInSeconds,
        string? ip = null)
    {
        var expires   = DateTimeOffset.UtcNow.ToUnixTimeSeconds() + expiryInSeconds;
        var tokenData = expires + path + (ip ?? "") + secret;

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
        if (!string.IsNullOrEmpty(ip))
        {
            query["ip"] = ip;
        }

        return $"{hostname}{path}?{query}";
    }
}