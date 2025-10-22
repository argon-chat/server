namespace Argon.Features.MediaStorage;

using Flurl.Http;
using Flurl.Http.Newtonsoft;

public class KineticaFSApi(ILogger<IKineticaFSApi> logger, IOptions<KineticaFSApiOptions> options) : IKineticaFSApi, IDisposable
{
    private readonly IFlurlClient client = new FlurlClient(options.Value.Endpoint)
    {
        Settings =
        {
            JsonSerializer = new NewtonsoftJsonSerializer()
        }
    };
    private const string XApiTokenHeader = "X-Api-Token";

    public async Task<Guid> CreateUploadUrlAsync(uint? limitMb = null, string? regionId = null, CancellationToken ct = default)
    {
        try
        {
            limitMb  ??= options.Value.DefaultFileLimitMb;
            regionId ??= options.Value.DefaultRegionId;

            logger.LogInformation("Creating upload URL (region: {Region}, limit: {LimitMb} MB)...", regionId,
                limitMb);

            var response = await client
               .WithHeader(XApiTokenHeader, options.Value.ApiToken)
               .Request("/api/v1/file/")
               .AllowAnyHttpStatus()
               .PostJsonAsync(new
                {
                    regionId,
                    fileSizeLimit = limitMb
                }, cancellationToken: ct);

            if (!response.ResponseMessage.IsSuccessStatusCode)
            {
                logger.LogWarning("KineticaFS responded with HTTP {Status}: {Body}", response.StatusCode, await response.GetStringAsync());
                throw new HttpRequestException($"Failed to create upload URL: {response.StatusCode}");
            }

            var result = await response.GetJsonAsync<CreateUploadUrlResponse>();
            logger.LogInformation("Upload URL created successfully (TTL: {Ttl}s, Id: {Id})", result.ttl, result.url);

            return result.url;
        }
        catch (FlurlHttpTimeoutException ex)
        {
            logger.LogError(ex, "Timeout while creating upload URL.");
            throw;
        }
        catch (FlurlHttpException ex)
        {
            var content = await ex.GetResponseStringAsync();
            logger.LogError(ex, "HTTP error while creating upload URL: {Content}", content);
            throw;
        }
        catch (Exception ex)
        {
            logger.LogError(ex, "Unexpected error while creating upload URL.");
            throw;
        }
    }


    public async Task<Guid> FinalizeUploadUrlAsync(Guid blobId, CancellationToken ct = default)
    {
        try
        {
            logger.LogInformation("Finalizing upload {BlobId}...", blobId);

            var response = await client
               .WithHeader(XApiTokenHeader, options.Value.ApiToken)
               .Request($"/api/v1/file/{blobId}/finalize")
               .AllowAnyHttpStatus()
               .PostJsonAsync(new
                {
                }, cancellationToken: ct);

            if (!response.ResponseMessage.IsSuccessStatusCode)
            {
                logger.LogWarning("Finalize request failed with HTTP {Status}: {Body}", response.StatusCode, await response.GetStringAsync());
                throw new HttpRequestException($"Failed to finalize upload: {response.StatusCode}");
            }

            var result = await response.GetJsonAsync<FileFinalizeResponse>();

            if (!result.Finalized)
                logger.LogWarning("File {Id} not marked as finalized after response.", result.Id);

            logger.LogInformation("Upload finalized successfully. File: {Name} ({Size} bytes)", result.Name, result.FileSize);
            return result.Id;
        }
        catch (FlurlHttpTimeoutException ex)
        {
            logger.LogError(ex, "Timeout while finalizing upload {BlobId}.", blobId);
            throw;
        }
        catch (FlurlHttpException ex)
        {
            var content = await ex.GetResponseStringAsync();
            logger.LogError(ex, "HTTP error while finalizing upload {BlobId}: {Content}", blobId, content);
            throw;
        }
        catch (Exception ex)
        {
            logger.LogError(ex, "Unexpected error while finalizing upload {BlobId}.", blobId);
            throw;
        }
    }

    private record CreateUploadUrlResponse(Guid url, int ttl);

    private record FileFinalizeResponse(
        [property: JsonProperty("id")] Guid Id,
        [property: JsonProperty("created_at")] DateTime CreatedAt,
        [property: JsonProperty("updated_at")] DateTime UpdatedAt,
        [property: JsonProperty("bucket_id")] Guid BucketId,
        [property: JsonProperty("name")] string Name,
        [property: JsonProperty("path")] string Path,
        [property: JsonProperty("file_size")] long FileSize,
        [property: JsonProperty("content_type")]
        string ContentType,
        [property: JsonProperty("checksum")] string Checksum,
        [property: JsonProperty("finalized")] bool Finalized,
        [property: JsonProperty("file_size_limit")]
        long FileSizeLimit,
        [property: JsonProperty("metadata")] string Metadata
    );

    public void Dispose()
        => client.Dispose();
}

public interface IKineticaFSApi
{
    Task<Guid> CreateUploadUrlAsync(uint? limitMb = null, string? regionId = null, CancellationToken ct = default);
    Task<Guid> FinalizeUploadUrlAsync(Guid blobId, CancellationToken ct = default);
}

public record KineticaFSApiOptions
{
    public required string Endpoint           { get; set; }
    public          uint   DefaultFileLimitMb { get; set; } = 100;
    public required string ApiToken           { get; set; }
    public required string DefaultRegionId    { get; set; }
}

public static class KineticaExtensions
{
    public static void AddKineticaFSApi(this WebApplicationBuilder builder)
    {
        builder.Services.Configure<KineticaFSApiOptions>(builder.Configuration.GetSection("KineticaFS"));
        builder.Services.AddScoped<IKineticaFSApi, KineticaFSApi>();
    }
}